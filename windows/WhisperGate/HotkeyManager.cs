using System;
using System.Runtime.InteropServices;
using System.Threading;

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
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private IntPtr _hWnd;
    private WndProcDelegate? _wndProc; // prevent GC
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
        // Create a message-only window for WM_HOTKEY
        _wndProc = WndProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "WhisperGateHotkey"
        };
        RegisterClass(ref wc);
        _hWnd = CreateWindowEx(0, "WhisperGateHotkey", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        // Escape always cancels
        RegisterHotKey(_hWnd, HOTKEY_ESCAPE, 0, 0x1B);

        // Toggle Recording
        if (_settings.ToggleRecordingKey != 0 && !IsModifierOnly(_settings.ToggleRecordingKey))
            RegisterHotKey(_hWnd, HOTKEY_TOGGLE, (uint)_settings.ToggleRecordingModifiers, (uint)_settings.ToggleRecordingKey);

        // PTT
        if (_settings.PushToTalkKey != 0)
        {
            if (IsModifierOnly(_settings.PushToTalkKey))
            {
                _pttWasDown = false;
                _pollTimer = new Timer(PollModifier, null, 0, 50);
            }
            else
            {
                RegisterHotKey(_hWnd, HOTKEY_PTT, (uint)_settings.PushToTalkModifiers, (uint)_settings.PushToTalkKey);
            }
        }
    }

    public void Unregister()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_hWnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hWnd, HOTKEY_TOGGLE);
            UnregisterHotKey(_hWnd, HOTKEY_ESCAPE);
            UnregisterHotKey(_hWnd, HOTKEY_PTT);
            DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            OnHotKey(wParam.ToInt32());
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void PollModifier(object? state)
    {
        bool isDown = (GetAsyncKeyState(_settings.PushToTalkKey) & 0x8000) != 0;
        if (isDown && !_pttWasDown)
        {
            _pttWasDown = true;
            _engine.EngageGate();
            App.Instance.UpdateTrayTooltip("WhisperGate - Active");
        }
        else if (!isDown && _pttWasDown)
        {
            _pttWasDown = false;
            if (!_isRecordingToggled)
            {
                _engine.DisengageGate();
                App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
            }
        }
    }

    private void OnHotKey(int id)
    {
        switch (id)
        {
            case HOTKEY_TOGGLE:
                _isRecordingToggled = !_isRecordingToggled;
                if (_isRecordingToggled)
                {
                    _engine.EngageGate();
                    App.Instance.UpdateTrayTooltip("WhisperGate - Active");
                }
                else
                {
                    _engine.DisengageGate();
                    _pttWasDown = false;
                    App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
                }
                break;

            case HOTKEY_ESCAPE:
                _isRecordingToggled = false;
                _pttWasDown = false;
                _engine.DisengageGate();
                App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
                break;

            case HOTKEY_PTT:
                // For non-modifier PTT, RegisterHotKey only fires on press, not release
                // So we use it as a toggle fallback
                if (!_pttWasDown)
                {
                    _pttWasDown = true;
                    _engine.EngageGate();
                    App.Instance.UpdateTrayTooltip("WhisperGate - Active");
                }
                break;
        }
    }

    private static bool IsModifierOnly(int vk) => Array.IndexOf(ModifierVKs, vk) >= 0;

    public void Dispose() => Unregister();
}
