using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WhisperGate;

/// Hotkey detection:
/// - For ALL shortcuts, use GetAsyncKeyState polling on DispatcherTimer (UI thread)
/// - This avoids RegisterHotKey conflicts with superwhisper
/// - Works for both modifier-only keys and combo keys
public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private DispatcherTimer? _pollTimer;
    private bool _pttWasDown;
    private bool _recWasDown;
    private bool _escWasDown;
    private bool _isRecordingToggled;

    public HotkeyManager(Settings settings, NoiseGateEngine engine)
    {
        _settings = settings;
        _engine = engine;
    }

    public void Register()
    {
        _pttWasDown = false;
        _recWasDown = false;
        _escWasDown = false;
        _isRecordingToggled = false;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // 20Hz
        };
        _pollTimer.Tick += Poll;
        _pollTimer.Start();
    }

    public void Unregister()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void Poll(object? sender, EventArgs e)
    {
        // Check Escape
        bool escDown = IsKeyDown(0x1B); // VK_ESCAPE
        if (escDown && !_escWasDown)
        {
            _isRecordingToggled = false;
            _pttWasDown = false;
            _engine.DisengageGate();
            App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
        }
        _escWasDown = escDown;

        // Check Toggle Recording (combo key like Ctrl+Shift+Tab)
        if (_settings.ToggleRecordingKey != 0)
        {
            bool recDown = IsComboDown(_settings.ToggleRecordingKey, _settings.ToggleRecordingModifiers);
            if (recDown && !_recWasDown)
            {
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
            }
            _recWasDown = recDown;
        }

        // Check PTT (modifier-only or combo)
        if (_settings.PushToTalkKey != 0)
        {
            bool pttDown = IsKeyDown(_settings.PushToTalkKey);
            if (pttDown && !_pttWasDown)
            {
                _pttWasDown = true;
                _engine.EngageGate();
                App.Instance.UpdateTrayTooltip("WhisperGate - Active");
            }
            else if (!pttDown && _pttWasDown)
            {
                _pttWasDown = false;
                if (!_isRecordingToggled)
                {
                    _engine.DisengageGate();
                    App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
                }
            }
        }
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsComboDown(int vk, int mods)
    {
        if (!IsKeyDown(vk)) return false;
        if ((mods & 0x0001) != 0 && !IsKeyDown(0xA4) && !IsKeyDown(0xA5)) return false; // ALT
        if ((mods & 0x0002) != 0 && !IsKeyDown(0xA2) && !IsKeyDown(0xA3)) return false; // CTRL
        if ((mods & 0x0004) != 0 && !IsKeyDown(0xA0) && !IsKeyDown(0xA1)) return false; // SHIFT
        return true;
    }

    public void Dispose() => Unregister();
}
