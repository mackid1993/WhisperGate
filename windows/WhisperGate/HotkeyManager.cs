using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WhisperGate;

class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_ESCAPE = 2;

    // Modifier-only VK codes
    private static readonly int[] ModifierVKs = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private HotkeyWindow? _window;
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
        _window = new HotkeyWindow(this);

        // Escape always cancels
        RegisterHotKey(_window.Handle, HOTKEY_ESCAPE, 0, 0x1B);

        // Toggle Recording (combo like Ctrl+Shift+Tab)
        if (_settings.ToggleRecordingKey != 0 && !IsModifierOnly(_settings.ToggleRecordingKey))
        {
            RegisterHotKey(_window.Handle, HOTKEY_TOGGLE, (uint)_settings.ToggleRecordingModifiers,
                          (uint)_settings.ToggleRecordingKey);
        }

        // PTT — modifier-only uses polling
        if (_settings.PushToTalkKey != 0)
        {
            if (IsModifierOnly(_settings.PushToTalkKey))
            {
                _pttWasDown = false;
                _pollTimer = new Timer(PollModifier, null, 0, 50);
            }
            else
            {
                RegisterHotKey(_window.Handle, 3, (uint)_settings.PushToTalkModifiers,
                              (uint)_settings.PushToTalkKey);
            }
        }
    }

    public void Unregister()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_window != null)
        {
            UnregisterHotKey(_window.Handle, HOTKEY_TOGGLE);
            UnregisterHotKey(_window.Handle, HOTKEY_ESCAPE);
            UnregisterHotKey(_window.Handle, 3);
            _window.Dispose();
            _window = null;
        }
    }

    private void PollModifier(object? state)
    {
        int vk = _settings.PushToTalkKey;
        bool isDown = (GetAsyncKeyState(vk) & 0x8000) != 0;

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

    internal void OnHotKey(int id)
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

            case 3: // PTT combo key pressed
                _pttWasDown = true;
                _engine.EngageGate();
                App.Instance.UpdateTrayTooltip("WhisperGate - Active");
                break;
        }
    }

    private static bool IsModifierOnly(int vk) => Array.IndexOf(ModifierVKs, vk) >= 0;

    public void Dispose()
    {
        Unregister();
    }

    private class HotkeyWindow : IDisposable
    {
        private readonly HotkeyManager _manager;
        private readonly System.Windows.Forms.NativeWindow _nativeWindow;

        public IntPtr Handle => _nativeWindow.Handle;

        public HotkeyWindow(HotkeyManager manager)
        {
            _manager = manager;
            _nativeWindow = new InnerWindow(manager);
            _nativeWindow.CreateHandle(new System.Windows.Forms.CreateParams());
        }

        public void Dispose() => _nativeWindow.DestroyHandle();

        private class InnerWindow : System.Windows.Forms.NativeWindow
        {
            private readonly HotkeyManager _manager;
            public InnerWindow(HotkeyManager m) => _manager = m;
            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_HOTKEY) _manager.OnHotKey(m.WParam.ToInt32());
                base.WndProc(ref m);
            }
        }
    }
}
