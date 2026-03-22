# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. It monitors the same hotkeys as superwhisper and reduces the system mic input level when you're not speaking, filtering out background noise (TV, music, conversations). Available for macOS and Windows.

## Current State (2026-03-22)

### macOS — Working
- SwiftUI menu bar app with popover UI
- Carbon RegisterEventHotKey for combo shortcuts (no permissions needed)
- CGEventSource.flagsState polling for modifier-only shortcuts (no permissions needed)
- Only requires Microphone permission
- Auto-syncs shortcuts from superwhisper UserDefaults (`com.superduper.superwhisper`)
- AudioQueue-based mic capture (only runs during hotkey hold)
- Energy-based gate: 30% volume reduction when gating, 300ms hold time
- Setup window on first launch (mic permission + shortcut sync)
- Threshold slider (-60 to -20 dB)
- Escape key cancels dictation
- Lazy popover (tears down on close, zero CPU when idle)
- Universal binary (arm64 + x86_64)
- App icon generated via build script

### Windows — Working
- WPF app with dark Fluent-style UI
- System tray via Hardcodet.NotifyIcon.Wpf
- WH_KEYBOARD_LL low-level keyboard hook (sees all key events, no conflicts with superwhisper)
- NAudio for mic capture, Windows CoreAudio (MMDevice) for volume control
- Reads superwhisper preferences from `%APPDATA%/com.superwhisper.app/preferences.json`
- Comprehensive key name to VK code mapping (all standard keys supported)
- Same gate logic: 30% reduction, 300ms hold, threshold slider
- Tray icon changes color: gray (standby), green (active), amber (gating)
- Start at login via Windows Registry Run key
- Settings window with threshold slider, shortcut display, sync button
- Launches silently to tray (no window on startup)
- Self-contained single-directory publish via build.bat

### Architecture

#### Gate Logic (shared across platforms)
```
Hotkey pressed -> mic volume reduced to 30%
  -> Voice detected (above threshold) -> restore full volume
  -> Voice stops (300ms hold) -> reduce to 30% again
Hotkey released or Escape -> restore original volume
```

- Threshold: user-adjustable, -60 to -20 dB
- Reduction: fixed 30% (~10dB) — gentle, no choppy artifacts
- Hold time: 300ms — keeps gate open during natural pauses
- Detection: energy-based RMS, continuously monitored
- AudioQueue reads post-volume audio on both platforms

#### macOS Hotkey Detection
- Carbon RegisterEventHotKey for combo shortcuts (event-driven, zero CPU)
- CGEventSource.flagsState polling at 4Hz for modifier-only shortcuts
- Device-dependent flag bits distinguish left/right modifiers
- No Accessibility or Input Monitoring permissions needed

#### Windows Hotkey Detection
- WH_KEYBOARD_LL low-level keyboard hook (sees all events before any app)
- GetAsyncKeyState for modifier checks in combo shortcuts
- CallNextHookEx always passes events through (observe only)
- Dictionary-based key name to VK code conversion

#### superwhisper Integration
- macOS: reads from UserDefaults (`com.superduper.superwhisper`)
  - KeyboardShortcuts library format: `{"carbonKeyCode":49,"carbonModifiers":2048}`
  - Carbon modifier to CGEventFlags conversion
- Windows: reads from `%APPDATA%/com.superwhisper.app/preferences.json`
  - Tauri format: `"ControlRight"`, `"Control+Shift+Tab"`
  - String key names to Win32 VK codes conversion

### File Structure
```
macos/
  Sources/
    WhisperGateApp.swift          — App entry, MenuBarExtra
    AppState.swift                — State management, UserDefaults
    NoiseGateEngine.swift         — AudioQueue capture, gate logic
    HotkeyMonitor.swift           — Carbon hotkeys + modifier polling
    AudioDeviceManager.swift      — CoreAudio device enumeration
    PopoverView.swift             — Menu bar popover UI
    SetupView.swift               — First-launch setup window
    CalibrateButton.swift         — Threshold calibration helper
    SuperWhisperIntegration.swift — Reads superwhisper preferences
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
    SuperWhisperIntegration.cs     — Reads preferences.json
    Settings.cs                    — JSON settings persistence
    WhisperGate.csproj             — .NET 8 / WPF project
    icon.ico                       — App icon
  build.bat
```

### Key Decisions
- **dB reduction instead of mute**: no peek timer needed, continuous monitoring
- **30% fixed reduction**: gentle enough to avoid audio artifacts, strong enough to suppress noise
- **300ms hold time**: prevents gate from closing during natural speech pauses
- **No calibration required**: user sets threshold manually via slider
- **Carbon hotkeys on macOS**: no permissions needed (no Accessibility, no Input Monitoring)
- **Low-level keyboard hook on Windows**: sees all events, no conflicts with superwhisper
- **Lazy popover on macOS**: tears down view hierarchy on close, zero CPU
- **Tray-only launch on Windows**: no window on startup, settings on demand

### Known Limitations
- Gate cannot remove noise underneath speech (only during pauses)
- Hold time creates a brief window where background noise can leak after speech stops
- macOS: ad-hoc signing means Accessibility permission (if ever needed) resets on rebuild
- Windows: settings.json caches VK codes — must delete after changing superwhisper shortcuts if sync doesn't pick them up

## Build Requirements
- **macOS**: Xcode Command Line Tools (`xcode-select --install`)
- **Windows**: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Repository
https://github.com/mackid1993/WhisperGate
