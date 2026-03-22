using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;

namespace WhisperGate;

class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_ESCAPE = 2;
    private const int HOTKEY_PTT = 3;
    private static readonly int[] ModifierVKs = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private HwndSource? _hwndSource;
    private Timer? _pollTimer;
    private bool _pttWasDown;
    private bool _isRecordingToggled;

    public HotkeyManager(Settings settings, NoiseGateEngine engine)
    {
        _settings = settings;
        _engine = engine;
    }

    public void Register()
    {
        // Create a message-only window via WPF's HwndSource
        _hwndSource = new HwndSource(new HwndSourceParameters("WhisperGateHotkey")
        {
            Width = 0, Height = 0,
            PositionX = -100, PositionY = -100,
            WindowStyle = 0
        });
        _hwndSource.AddHook(WndProc);

        var hWnd = _hwndSource.Handle;
        RegisterHotKey(hWnd, HOTKEY_ESCAPE, 0, 0x1B);

        if (_settings.ToggleRecordingKey != 0 && !IsModifierOnly(_settings.ToggleRecordingKey))
            RegisterHotKey(hWnd, HOTKEY_TOGGLE, (uint)_settings.ToggleRecordingModifiers, (uint)_settings.ToggleRecordingKey);

        if (_settings.PushToTalkKey != 0)
        {
            if (IsModifierOnly(_settings.PushToTalkKey))
            {
                _pttWasDown = false;
                _pollTimer = new Timer(PollModifier, null, 0, 50);
            }
            else
            {
                RegisterHotKey(hWnd, HOTKEY_PTT, (uint)_settings.PushToTalkModifiers, (uint)_settings.PushToTalkKey);
            }
        }
    }

    public void Unregister()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_hwndSource != null)
        {
            var hWnd = _hwndSource.Handle;
            UnregisterHotKey(hWnd, HOTKEY_TOGGLE);
            UnregisterHotKey(hWnd, HOTKEY_ESCAPE);
            UnregisterHotKey(hWnd, HOTKEY_PTT);
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            OnHotKey(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    private void PollModifier(object? state)
    {
        bool isDown = (GetAsyncKeyState(_settings.PushToTalkKey) & 0x8000) != 0;
        if (isDown && !_pttWasDown)
        {
            _pttWasDown = true;
            _engine.EngageGate();
            App.Instance.Dispatcher.Invoke(() => App.Instance.UpdateTrayTooltip("WhisperGate - Active"));
        }
        else if (!isDown && _pttWasDown)
        {
            _pttWasDown = false;
            if (!_isRecordingToggled)
            {
                _engine.DisengageGate();
                App.Instance.Dispatcher.Invoke(() => App.Instance.UpdateTrayTooltip("WhisperGate - Standby"));
            }
        }
    }

    private void OnHotKey(int id)
    {
        switch (id)
        {
            case HOTKEY_TOGGLE:
                _isRecordingToggled = !_isRecordingToggled;
                if (_isRecordingToggled) { _engine.EngageGate(); App.Instance.UpdateTrayTooltip("WhisperGate - Active"); }
                else { _engine.DisengageGate(); _pttWasDown = false; App.Instance.UpdateTrayTooltip("WhisperGate - Standby"); }
                break;
            case HOTKEY_ESCAPE:
                _isRecordingToggled = false; _pttWasDown = false;
                _engine.DisengageGate(); App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
                break;
        }
    }

    private static bool IsModifierOnly(int vk) => Array.IndexOf(ModifierVKs, vk) >= 0;
    public void Dispose() => Unregister();
}
