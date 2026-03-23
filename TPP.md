# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. It monitors the same hotkeys as superwhisper and reduces the system mic input level when you're not speaking, filtering out background noise (TV, music, conversations). Available for macOS and Windows.

## Current State (2026-03-23)

### Status: v1.0.0 Released + Virtual Mic Driver IN PROGRESS
Both platforms working. Binaries published on GitHub.
Virtual mic driver (HAL plugin) partially implemented — loads but has CPU spike and audio passthrough issues.

## Active Work: Virtual Mic Driver (macOS)

### Goal
Replace volume manipulation with chunk replacement via a virtual audio device. When the gate is closed, superwhisper receives true silence (zero bytes) instead of reduced-volume audio that causes STT hallucinations.

### Architecture
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
[~/.whispergate_audio.buf - mmap'd ring buffer file]
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

### What Works
- [x] HAL plugin compiles (C, universal binary arm64+x86_64)
- [x] Plugin loads in coreaudiod (confirmed via logs: Factory called, Initialize called)
- [x] "WhisperGate Mic" appears as a selectable input device in superwhisper
- [x] SharedRingBuffer.swift creates mmap'd file at ~/.whispergate_audio.buf (confirmed fd=3)
- [x] NoiseGateEngine writes audio/silence to ring buffer based on gate state
- [x] Driver installer (install/uninstall via osascript with admin privileges)
- [x] Setup UI with driver toggle and explanation
- [x] build.sh compiles and bundles the HAL plugin

### What's Broken — MUST FIX
1. **CPU spike from GetZeroTimeStamp** — The timestamp advancement logic causes coreaudiod to spin. Tried single-advance-per-call (BlackHole pattern) and math-based jump — both spike. The `gHostTicksPerFrame` calculation or the advancement condition may be wrong. COMPARE CAREFULLY with BlackHole's working implementation at `/tmp/BlackHole.c` lines 4387-4450.

2. **No audio passes through the virtual mic** — The driver's DoIOOperation reads from the ring buffer but superwhisper hears silence. Possible causes:
   - The ring buffer read/write positions may be out of sync (app writes at its own rate, driver reads at HAL's rate — different clocks)
   - The driver's `open()` call to `~/.whispergate_audio.buf` may fail from coreaudiod's sandbox (errno=2 was seen when file didn't exist yet; need to verify it works when file DOES exist)
   - The mmap may not be working cross-process between the app and the driver

3. **Device shows as headphones/output in some contexts** — Fixed `CanBeDefaultDevice=0` and input-only scope, but may need more work.

### Failed Approaches (Do NOT Retry)
- **POSIX shm_open** — coreaudiod sandbox blocks it. The driver process (running as _coreaudiod user) cannot access POSIX shared memory created by the app. Use file-based mmap instead.
- **Volume manipulation with mute + pulsed detection** — Clips first syllable of speech. Up to 250ms latency between pulses means beginning of utterances get cut. Abandoned.
- **Volume manipulation with very low gated volume (1%)** — Still causes STT hallucinations. Not low enough for true silence.
- **Three-state gate (open/gated/muted)** — Overcomplicated, introduced more bugs than it solved. The two-state gate (open/closed) with volume fallback works.
- **Plugin Owner = kAudioObjectSystemObject** — Must be kAudioObjectUnknown (per BlackHole).
- **Forward-declaring static gDriverVtable then redefining** — C tentative definitions technically work but caused confusion. Use single definition at bottom with forward ref to gDriverRef only.
- **shm_open retry in DoIOOperation** — Causes CPU overhead on the IO hot path. Use lazy open instead.
- **Ring buffer creation only on gate engage** — Race condition: driver StartIO fires before gate engages, so file doesn't exist. Create at app launch instead.

### Key Discoveries (Lore)
- **coreaudiod sandbox**: The HAL plugin runs in its own process (`com.apple.audio.Core-Audio-Driver-Service.helper`). It's sandboxed but CAN read files from the user's home directory. It CANNOT use POSIX shm_open. It runs as `_coreaudiod` user (home=/var/empty), so use `SCDynamicStoreCopyConsoleUser()` to find the real user's home.
- **AMFI warning is not a blocker**: Ad-hoc signed drivers log `AMFI: adhoc signed` and `not valid` warnings but still load and run. No developer certificate needed.
- **Plugin loading**: coreaudiod scans ONLY `/Library/Audio/Plug-Ins/HAL/` (NOT ~/Library). Needs admin to install. After install, `killall coreaudiod` reloads (launchctl kickstart blocked by SIP).
- **GetZeroTimeStamp is called VERY frequently** — Any bug here causes 100% CPU. Must be O(1), no loops, no syscalls.
- **BlackHole reference implementation** saved at `/tmp/BlackHole.c` (4620 lines, MIT licensed). Key sections: vtable (405-432), QueryInterface (636-676), Initialize (729-795), HasProperty dispatch (964-1000), GetPlugInPropertyData (1397-1558), GetDevicePropertyData (2155-2271), IO ops (4302-4620).
- **AudioServerPlugIn_LoadingConditions** key in Info.plist is required — use `IOProviderClass: IOPlatformExpertDevice`.
- **Stream direction 1 = input** (data flows from device to app). Our device is input-only.
- **kAudioDevicePropertyDeviceCanBeDefaultDevice must be 0** — Otherwise every app grabs the virtual mic as default, causing system-wide audio issues.
- **NSHomeDirectory()** returns correct path in non-sandboxed app. `NSUserName()` also works for building the path.
- **DriverInstaller.runPrivileged** uses `osascript -e 'do shell script "..." with administrator privileges'` via Process. Must use `waitUntilExit()` for synchronous operations but dispatch to background for UI responsiveness.

### Files Created This Session
```
macos/HALPlugin/
  WhisperGateDriver.c    — AudioServerPlugin implementation (~500 lines C)
  Info.plist             — Plugin bundle metadata with factory UUID + loading conditions
macos/Sources/
  SharedRingBuffer.swift — mmap'd file ring buffer for app<->driver IPC
  DriverInstaller.swift  — Install/uninstall driver with admin privileges
```

### Files Modified This Session
- `macos/Sources/NoiseGateEngine.swift` — Added ring buffer writing in onBuffer, dual-mode (volume fallback + virtual mic)
- `macos/Sources/AppState.swift` — Added virtualMicEnabled toggle, driver state sync
- `macos/Sources/PopoverView.swift` — Added virtual mic driver toggle in settings
- `macos/Sources/SetupView.swift` — Added Step 3 for driver installation
- `macos/Sources/HotkeyMonitor.swift` — Dynamic Escape key registration (only when gate active)
- `macos/build.sh` — Added HAL plugin compilation, signing, bundling
- `windows/WhisperGate/HotkeyManager.cs` — Escape only cancels when gate active

### Next Steps (Priority Order)
1. **Fix GetZeroTimeStamp CPU spike** — Study BlackHole's exact implementation. The issue may be in `gHostTicksPerFrame` calculation or the single-advance condition. Consider copying BlackHole's code verbatim and adapting minimally.
2. **Fix audio passthrough** — Verify the driver can actually mmap the file by checking logs after app creates it. The read/write position SPSC pattern should work if the file is accessible. May need to add logging to DoIOOperation to confirm data is being read.
3. **Test end-to-end** — Once both fixes are in, test: engage gate -> speak -> verify superwhisper transcribes -> stop speaking -> verify true silence (no hallucinations).
4. **Commit and update TPP** — Once working, commit the HAL plugin feature as a new version.

---

## Stable Features (v1.0.0)

### macOS — Working
- SwiftUI menu bar app with popover UI (lazy rendering, zero CPU when closed)
- Carbon RegisterEventHotKey for combo shortcuts + CGEventSource.flagsState polling for modifier-only shortcuts
- No permissions needed for hotkey detection (no Accessibility, no Input Monitoring)
- Only requires Microphone permission
- Auto-syncs shortcuts from superwhisper UserDefaults (`com.superduper.superwhisper`)
- AudioQueue-based mic capture (only runs during hotkey hold)
- Energy-based gate with user-adjustable threshold and gated volume
- Escape key only intercepted when gate is active (dynamic registration)
- Universal binary (arm64 + x86_64)

### Windows — Working
- WPF app with dark Fluent-style UI
- System tray via Hardcodet.NotifyIcon.Wpf
- WH_KEYBOARD_LL low-level keyboard hook
- NAudio for mic capture, Windows CoreAudio (MMDevice) for volume control
- Escape only cancels when gate is active

### Gate Logic (both platforms)
```
Hotkey pressed -> mic volume set to gatedVolume
  -> Voice detected (above threshold) -> restore full volume
  -> Voice stops (300ms hold) -> reduce to gatedVolume again
Hotkey released or Escape -> restore original volume
```

### File Structure
```
macos/
  HALPlugin/
    WhisperGateDriver.c          — AudioServerPlugin (virtual mic)
    Info.plist                   — Plugin metadata
  Sources/
    WhisperGateApp.swift          — App entry, MenuBarExtra
    AppState.swift                — State management, virtualMicEnabled toggle
    NoiseGateEngine.swift         — AudioQueue capture, gate logic, ring buffer writing
    HotkeyMonitor.swift           — Carbon hotkeys + modifier polling, dynamic Escape
    AudioDeviceManager.swift      — CoreAudio device enumeration + volume/mute
    PopoverView.swift             — Menu bar popover UI with driver toggle
    SetupView.swift               — First-launch setup with driver install step
    SharedRingBuffer.swift        — mmap'd file ring buffer for IPC
    DriverInstaller.swift         — HAL plugin install/uninstall
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

## Build Requirements
- macOS: Xcode Command Line Tools (`xcode-select --install`)
- Windows: [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Repository
https://github.com/mackid1993/WhisperGate
