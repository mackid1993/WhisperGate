# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. It monitors the same hotkeys as superwhisper and reduces the system mic input level when you're not speaking, filtering out background noise (TV, music, conversations). Available for macOS and Windows.

## Current State (2026-03-22)

### Status: v1.0.0 Released
Both platforms working. Binaries published on GitHub.

### macOS — Working
- SwiftUI menu bar app with popover UI (lazy rendering, zero CPU when closed)
- Carbon RegisterEventHotKey for combo shortcuts + CGEventSource.flagsState polling for modifier-only shortcuts
- No permissions needed for hotkey detection (no Accessibility, no Input Monitoring)
- Only requires Microphone permission
- Auto-syncs shortcuts from superwhisper UserDefaults (`com.superduper.superwhisper`)
- AudioQueue-based mic capture (only runs during hotkey hold)
- Energy-based gate with user-adjustable threshold and gated volume
- Hardware mute quirk: Mac volume 0 still leaks ~20dB, so uses 0.1% floor instead
- Setup window on first launch
- Escape key cancels dictation
- Universal binary (arm64 + x86_64)

### Windows — Working
- WPF app with dark Fluent-style UI
- System tray via Hardcodet.NotifyIcon.Wpf
- WH_KEYBOARD_LL low-level keyboard hook (sees all key events, no conflicts with superwhisper)
- NAudio for mic capture, Windows CoreAudio (MMDevice) for volume control
- Reads superwhisper preferences from `%APPDATA%/com.superwhisper.app/preferences.json`
- Comprehensive key name to VK code mapping
- Tray icon changes color: gray (standby), green (active), amber (gating)
- Start at login via Windows Registry Run key
- Launches silently to tray
- Self-contained publish via build.bat

### Gate Logic (identical on both platforms)
```
Hotkey pressed -> mic volume set to gatedVolume
  -> Voice detected (above threshold) -> restore full volume
  -> Voice stops (300ms hold) -> reduce to gatedVolume again
Hotkey released or Escape -> restore original volume
```

- Threshold: user-adjustable, -60 to -20 dB
- Gated Volume: user-adjustable, 0% to 100% (default 30%)
- Hold time: 300ms
- Detection: energy-based RMS, continuously monitored
- Open threshold: threshold - 6 dB (4dB hysteresis over 10dB reduction estimate)
- lastSpeechTime starts at 0 (distant past) on engage so gate starts fully closed

### Platform Differences
- Mac: volume 0 leaks audio, uses 0.1% floor. Windows: volume 0 is true silence.
- Mac: Carbon RegisterEventHotKey + CGEventSource polling. Windows: WH_KEYBOARD_LL hook.
- Mac: superwhisper stores shortcuts in UserDefaults (carbon key codes). Windows: preferences.json (string key names like "ControlRight").
- Mac: AudioQueue with vDSP_measqv for RMS. Windows: NAudio WaveInEvent with manual RMS.

### Hotkey Detection
- macOS: Carbon RegisterEventHotKey (combo shortcuts) + CGEventSource.flagsState at 4Hz (modifier-only). Device-dependent flag bits for left/right distinction.
- Windows: WH_KEYBOARD_LL low-level keyboard hook. GetAsyncKeyState for modifier checks in combos. CallNextHookEx passes all events through.

### superwhisper Integration
- macOS: UserDefaults(`com.superduper.superwhisper`), KeyboardShortcuts format: `{"carbonKeyCode":49,"carbonModifiers":2048}`
- Windows: `%APPDATA%/com.superwhisper.app/preferences.json`, string format: `"ControlRight"`, `"Control+Shift+Tab"`

### File Structure
```
macos/
  Sources/
    WhisperGateApp.swift          — App entry, MenuBarExtra
    AppState.swift                — State management, UserDefaults, didFinishInit guard
    NoiseGateEngine.swift         — AudioQueue capture, gate logic
    HotkeyMonitor.swift           — Carbon hotkeys + modifier polling
    AudioDeviceManager.swift      — CoreAudio device enumeration + volume/mute
    PopoverView.swift             — Menu bar popover UI (lazy)
    SetupView.swift               — First-launch setup window (NSWindow)
    CalibrateButton.swift         — Calibration helper + LiveRecorder
    SuperWhisperIntegration.swift — Reads superwhisper UserDefaults
    VoiceProfile.swift            — Voice profile types
    LoginItemManager.swift        — Start at login (SMAppService)
  Resources/
    Info.plist
    WhisperGate.entitlements
  build.sh
  generate_icon.sh

windows/
  WhisperGate/
    App.xaml / App.xaml.cs         — WPF app, tray icon, lifecycle
    SettingsWindow.xaml / .cs      — Dark Fluent-style settings UI
    NoiseGateEngine.cs             — NAudio capture, CoreAudio volume
    HotkeyManager.cs              — WH_KEYBOARD_LL hook
    SuperWhisperIntegration.cs     — Reads preferences.json, key name dictionary
    Settings.cs                    — JSON settings persistence
    WhisperGate.csproj             — .NET 8 / WPF project
    icon.ico                       — App icon (Pillow-generated)
  build.bat
```

### Key Decisions
- dB reduction instead of mute (Mac volume 0 leaks, mute blocks own audio capture)
- User-adjustable gated volume (0-100%) for different mic sensitivities
- 300ms hold time prevents gate from closing during natural speech pauses
- No calibration required — user sets threshold and gated volume manually
- Carbon hotkeys on macOS — zero permissions needed
- Low-level keyboard hook on Windows — sees all events, no conflicts with superwhisper
- Lazy popover on macOS — tears down view on close, zero CPU
- didFinishInit guard on macOS — prevents didSet from overwriting saved settings during init
- lastSpeechTime = 0 on engage — gate starts fully closed, doesn't open for noise

### Known Limitations
- Gate cannot remove noise underneath speech (only during pauses)
- Hold time creates a brief window where background noise can leak after speech stops
- Whisper models hallucinate on near-silent audio — very low gated volume may cause gibberish transcription
- Mac built-in mic is very sensitive — may need lower gated volume than Windows
- Windows settings.json caches VK codes — delete it if sync doesn't pick up new shortcuts

## Build Requirements
- macOS: Xcode Command Line Tools (`xcode-select --install`)
- Windows: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Repository
https://github.com/mackid1993/WhisperGate
