using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperGate;

/// Global hotkey registration via Win32 RegisterHotKey.
/// For modifier-only shortcuts (like ControlRight), uses a low-level keyboard hook.
class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_PTT = 1;
    private const int HOTKEY_TOGGLE = 2;
    private const int HOTKEY_ESCAPE = 3;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private readonly TrayApp _app;
    private readonly HotkeyWindow _window;
    private Timer? _pollTimer;
    private bool _pttWasDown;
    private bool _isRecordingToggled;

    public HotkeyManager(Settings settings, NoiseGateEngine engine, TrayApp app)
    {
        _settings = settings;
        _engine = engine;
        _app = app;
        _window = new HotkeyWindow(this);
    }

    public void Register()
    {
        // Escape always cancels
        RegisterHotKey(_window.Handle, HOTKEY_ESCAPE, 0, (uint)Keys.Escape);

        // Toggle Recording (combo like Ctrl+Shift+Tab)
        if (_settings.ToggleRecordingKey != Keys.None && !IsModifierOnly(_settings.ToggleRecordingKey))
        {
            uint mods = KeysToWin32Mods(_settings.ToggleRecordingModifiers);
            RegisterHotKey(_window.Handle, HOTKEY_TOGGLE, mods, (uint)_settings.ToggleRecordingKey);
        }

        // PTT — if it's a modifier-only key (like ControlRight), use polling
        if (IsModifierOnly(_settings.PushToTalkKey))
        {
            _pttWasDown = false;
            _pollTimer = new Timer { Interval = 50 }; // 20Hz
            _pollTimer.Tick += PollModifier;
            _pollTimer.Start();
        }
        else if (_settings.PushToTalkKey != Keys.None)
        {
            uint mods = KeysToWin32Mods(_settings.PushToTalkModifiers);
            RegisterHotKey(_window.Handle, HOTKEY_PTT, mods, (uint)_settings.PushToTalkKey);
        }
    }

    public void Unregister()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
        UnregisterHotKey(_window.Handle, HOTKEY_PTT);
        UnregisterHotKey(_window.Handle, HOTKEY_TOGGLE);
        UnregisterHotKey(_window.Handle, HOTKEY_ESCAPE);
    }

    private void PollModifier(object? sender, EventArgs e)
    {
        int vk = (int)_settings.PushToTalkKey;
        bool isDown = (GetAsyncKeyState(vk) & 0x8000) != 0;

        if (isDown && !_pttWasDown)
        {
            _pttWasDown = true;
            _engine.EngageGate();
            _app.UpdateIcon(true, true);
        }
        else if (!isDown && _pttWasDown)
        {
            _pttWasDown = false;
            if (!_isRecordingToggled)
            {
                _engine.DisengageGate();
                _app.UpdateIcon(false, true);
            }
        }
    }

    internal void OnHotKey(int id)
    {
        switch (id)
        {
            case HOTKEY_TOGGLE:
                _isRecordingToggled = !_isRecordingToggled;
                if (_isRecordingToggled)
                {
                    _engine.EngageGate();
                    _app.UpdateIcon(true, true);
                }
                else
                {
                    _engine.DisengageGate();
                    _pttWasDown = false;
                    _app.UpdateIcon(false, true);
                }
                break;

            case HOTKEY_ESCAPE:
                _isRecordingToggled = false;
                _pttWasDown = false;
                _engine.DisengageGate();
                _app.UpdateIcon(false, true);
                break;
        }
    }

    private static bool IsModifierOnly(Keys key) =>
        key is Keys.RControlKey or Keys.LControlKey or Keys.RShiftKey or Keys.LShiftKey
            or Keys.RMenu or Keys.LMenu or Keys.LWin or Keys.RWin;

    private static uint KeysToWin32Mods(Keys mods)
    {
        uint m = 0;
        if ((mods & Keys.Alt) != 0) m |= 0x0001;
        if ((mods & Keys.Control) != 0) m |= 0x0002;
        if ((mods & Keys.Shift) != 0) m |= 0x0004;
        return m;
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

    // Hidden window to receive WM_HOTKEY messages
    private class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly HotkeyManager _manager;

        public HotkeyWindow(HotkeyManager manager)
        {
            _manager = manager;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                _manager.OnHotKey(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
