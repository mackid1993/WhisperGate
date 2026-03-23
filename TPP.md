# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. It monitors the same hotkeys as superwhisper and reduces the system mic input level when you're not speaking, filtering out background noise (TV, music, conversations). Available for macOS and Windows.

## Current State (2026-03-23)

### Status: v1.1.0 Released
Both platforms working. Virtual mic driver shipped and tested on multiple Macs.

### macOS — Working
- SwiftUI menu bar app with popover UI (lazy rendering, zero CPU when closed)
- Carbon RegisterEventHotKey for combo shortcuts + CGEventSource.flagsState polling for modifier-only shortcuts
- No permissions needed for hotkey detection (no Accessibility, no Input Monitoring)
- Only requires Microphone permission
- Auto-syncs shortcuts from superwhisper UserDefaults (`com.superduper.superwhisper`)
- AudioQueue-based mic capture (only runs during hotkey hold)
- Energy-based gate with user-adjustable threshold and gated volume
- **Virtual mic driver (AudioServerPlugin)** — true silence via chunk replacement
- Escape key only intercepted when gate is active (dynamic registration)
- Universal binary (arm64 + x86_64)

### Windows — Working
- WPF app with dark Fluent-style UI
- System tray via Hardcodet.NotifyIcon.Wpf
- WH_KEYBOARD_LL low-level keyboard hook
- NAudio for mic capture, Windows CoreAudio (MMDevice) for volume control
- Escape only cancels when gate is active
- No virtual mic needed — Windows volume 0 is true silence

### Virtual Mic Driver (macOS)

#### Architecture
```
Real Mic (full volume, always)
    |
    v
[AudioQueue in WhisperGate app] -> RMS detection -> gate state machine
    |
    v
[Chunk decision]
  gate open:  copy raw samples to mmap'd file
  gate closed: write zeros to mmap'd file
    |
    v
[/tmp/whispergate_audio.buf - mmap'd ring buffer file]
    |
    v
[WhisperGateAudio.driver - HAL plugin in coreaudiod]
    |
    v
"WhisperGate Mic" virtual input device
    |
    v
superwhisper reads from it
```

#### How It Works
- App creates mmap'd file at `/tmp/whispergate_audio.buf` on engine init
- AudioQueue callback writes raw samples (gate open) or zeros (gate closed) to ring buffer
- HAL plugin lazily opens the file in DoIOOperation, copies data into its own internal ring buffer
- Plugin serves audio to clients using `mSampleTime % kRing_Buffer_Frame_Size` positioning (BlackHole pattern)
- When app isn't running, plugin outputs silence

#### Driver Lifecycle
- Installed on first launch (setup screen) or via Settings toggle
- Lives at `/Library/Audio/Plug-Ins/HAL/WhisperGateAudio.driver` (requires admin)
- Persists across app restarts — superwhisper's mic selection is stable
- User can remove via Settings toggle
- `killall coreaudiod` reloads (launchctl kickstart blocked by SIP)

#### Key Technical Details
- `kDevice_RingBufferSize = 16384` for ZeroTimeStampPeriod (512 caused CPU spike)
- `kRing_Buffer_Frame_Size = 65536` for internal ring buffer
- POSIX `shm_open` blocked by coreaudiod sandbox — use file-based mmap at `/tmp/` instead
- File must be created with `umask(0)` or `FileManager.createFile(posixPermissions: 0o666)` — default umask strips read permissions for other users
- coreaudiod runs as `_coreaudiod` user (home=/var/empty) — cannot access `~/` paths due to TCC
- `/tmp/` is accessible cross-user and survives until reboot (file recreated on app launch)
- `kAudioDevicePropertyDeviceCanBeDefaultDevice = 0` — prevents every app from grabbing virtual mic as default
- AMFI warns about ad-hoc signing but does NOT block — driver loads and runs without developer certificate
- Tested on multiple Macs — works without Gatekeeper prompt when transferred directly (no quarantine flag)
- `DriverInstaller.runPrivileged` must run on background thread to avoid UI freeze during admin dialog
- On uninstall: stop engine, delete ring buffer file, remove driver, restart coreaudiod, recreate engine in volume fallback mode

### Gate Logic (both platforms)
```
Hotkey pressed -> gate engages
  Virtual mic mode: write audio/silence to ring buffer
  Volume fallback:  reduce mic volume to gatedVolume
  -> Voice detected (above threshold) -> pass audio / restore full volume
  -> Voice stops (hold time) -> write silence / reduce volume
Hotkey released or Escape -> restore original state
```

- Threshold: user-adjustable, -60 to -20 dB
- Gated Volume: user-adjustable, 0% to 100% (volume fallback mode only)
- Hold time: 300ms
- Detection: energy-based RMS via vDSP_measqv, continuously monitored
- Open threshold: threshold - 6 dB (hysteresis)
- Gated Volume slider hidden when virtual mic driver is installed

### Platform Differences
- Mac volume 0 leaks ~20dB → virtual mic driver provides true silence
- Windows volume 0 is true silence → no driver needed
- Mac: Carbon RegisterEventHotKey + CGEventSource polling. Windows: WH_KEYBOARD_LL hook.
- Mac: superwhisper stores shortcuts in UserDefaults. Windows: preferences.json.
- Mac: AudioQueue with vDSP_measqv for RMS. Windows: NAudio WaveInEvent with manual RMS.

### File Structure
```
macos/
  HALPlugin/
    WhisperGateDriver.c          — AudioServerPlugin (virtual mic, ~500 lines C)
    Info.plist                   — Plugin metadata + loading conditions
  Sources/
    WhisperGateApp.swift          — App entry, MenuBarExtra
    AppState.swift                — State management, virtualMicEnabled toggle
    NoiseGateEngine.swift         — AudioQueue capture, gate logic, ring buffer writing
    HotkeyMonitor.swift           — Carbon hotkeys + modifier polling, dynamic Escape
    AudioDeviceManager.swift      — CoreAudio device enumeration + volume/mute
    PopoverView.swift             — Menu bar popover UI with driver toggle
    SetupView.swift               — First-launch setup with driver install step
    SharedRingBuffer.swift        — mmap'd file ring buffer for IPC
    DriverInstaller.swift         — HAL plugin install/uninstall with admin privileges
    CalibrateButton.swift         — Calibration helper + LiveRecorder
    SuperWhisperIntegration.swift — Reads superwhisper UserDefaults
    VoiceProfile.swift            — Voice profile types
    LoginItemManager.swift        — Start at login (SMAppService)
  Resources/
    Info.plist
    WhisperGate.entitlements
  build.sh                        — Builds app + HAL plugin (universal)
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
  build.bat
```

### Failed Approaches (Do NOT Retry)
- **POSIX shm_open for IPC** — coreaudiod sandbox blocks it. Use file-based mmap at `/tmp/`.
- **Ring buffer file in ~/home** — coreaudiod can't access due to TCC. Use `/tmp/`.
- **`open()` via `@_silgen_name`** — mode argument gets mangled. Use `FileManager.createFile` then `Darwin.open`.
- **Volume mute + pulsed detection** — Clips first syllable. Abandoned.
- **Three-state gate (open/gated/muted)** — Overcomplicated. Two-state with volume fallback works.
- **`kDevice_RingBufferSize = 512`** — Way too small, causes GetZeroTimeStamp to spin CPU. Use 16384.
- **Plugin Owner = kAudioObjectSystemObject** — Must be kAudioObjectUnknown.
- **`CanBeDefaultDevice = 1`** — Every app grabs virtual mic as default. Must be 0.
- **Scope-dependent properties returning true for output** — Input-only device must not claim output capabilities.
- **`DriverInstaller.install()` on main thread** — Freezes UI during admin dialog. Must dispatch to background.

## Build Requirements
- macOS: Xcode Command Line Tools (`xcode-select --install`)
- Windows: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Repository
https://github.com/mackid1993/WhisperGate
