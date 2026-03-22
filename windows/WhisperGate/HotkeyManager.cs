using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WhisperGate;

/// Uses a low-level keyboard hook (WH_KEYBOARD_LL) which sees ALL key events
/// before any app can consume them. No conflicts with superwhisper.
public class HotkeyManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly Settings _settings;
    private readonly NoiseGateEngine _engine;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc; // prevent GC
    private bool _pttWasDown;
    private bool _recWasDown;
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
        _isRecordingToggled = false;

        _hookProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
            App.Log($"SetWindowsHookEx FAILED: error {Marshal.GetLastWin32Error()}");
        else
            App.Log($"Keyboard hook installed: {_hookId}");
    }

    public void Unregister()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)kbd.vkCode;
            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Escape cancels
            if (vk == 0x1B && isDown)
            {
                App.Log("Escape pressed");
                _isRecordingToggled = false;
                _pttWasDown = false;
                _engine.DisengageGate();
                App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
            }

            // PTT (e.g. Right Control = 0xA3)
            if (_settings.PushToTalkKey != 0 && vk == _settings.PushToTalkKey)
            {
                if (isDown && !_pttWasDown)
                {
                    _pttWasDown = true;
                    App.Log($"PTT DOWN (vk=0x{vk:X})");
                    _engine.EngageGate();
                    App.Instance.UpdateTrayTooltip("WhisperGate - Active");
                }
                else if (isUp && _pttWasDown)
                {
                    _pttWasDown = false;
                    App.Log("PTT UP");
                    if (!_isRecordingToggled)
                    {
                        _engine.DisengageGate();
                        App.Instance.UpdateTrayTooltip("WhisperGate - Standby");
                    }
                }
            }

            // Toggle Recording (e.g. Ctrl+Shift+Tab)
            if (_settings.ToggleRecordingKey != 0 && vk == _settings.ToggleRecordingKey && isDown)
            {
                // Check modifiers
                bool modsOk = true;
                int mods = _settings.ToggleRecordingModifiers;
                if ((mods & 0x0002) != 0) // CTRL
                    modsOk &= (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                if ((mods & 0x0004) != 0) // SHIFT
                    modsOk &= (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                if ((mods & 0x0001) != 0) // ALT
                    modsOk &= (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;

                if (modsOk && !_recWasDown)
                {
                    _recWasDown = true;
                    _isRecordingToggled = !_isRecordingToggled;
                    App.Log($"Toggle Recording: {(_isRecordingToggled ? "ON" : "OFF")}");
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
            }
            if (_settings.ToggleRecordingKey != 0 && vk == _settings.ToggleRecordingKey && isUp)
            {
                _recWasDown = false;
            }
        }

        // Always pass the event through — we're just observing, not blocking
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Unregister();
}
