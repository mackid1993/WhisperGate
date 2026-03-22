# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight macOS menu bar app that acts as a noise gate for superwhisper dictation. It monitors the same hotkeys as superwhisper and reduces the system mic input level when background noise is detected, restoring it when speech is detected. Uses proximity-based voice detection (FFT frequency analysis) to distinguish close-up speech from distant TV/background audio.

## Current State (2026-03-22)

### What Works
- Proximity-aware voice detection using FFT (bass/mid/high frequency band analysis)
- Carbon RegisterEventHotKey for combo shortcuts (Option+Space) — no permissions needed
- CGEventSource.flagsState polling for modifier-only shortcuts (Right Option) — no permissions needed
- Auto-syncs shortcuts from superwhisper preferences
- AudioQueue-based mic capture (only runs during hotkey hold)
- Gate starts CLOSED on engage — background noise muted immediately
- Peek mechanism at 1% volume every 150ms to detect speech onset
- Two-step calibration wizard (measure room noise, then measure voice)
- Threshold = peak noise + 3dB (95th percentile)
- Setup window on first launch (mic permission + shortcut sync)
- Mic volume restored on app startup (crash recovery)
- Universal binary (arm64 + x86_64)
- Lazy popover (tears down view hierarchy on close, zero CPU when closed)
- No logging overhead in production

### Known Issues
- Hold time (200ms) may still leak some TV audio after speech stops
- Calibration may need manual threshold adjustment for very loud environments
- CPU usage should be monitored — FFT adds compute during active gating
- The proximity detection algorithm needs real-world testing and tuning

### Architecture

```
WhisperGateApp.swift          — App entry, MenuBarExtra, setup flow
AppState.swift                — @Observable state, UserDefaults persistence, KeyCombo type
NoiseGateEngine.swift         — AudioQueue, FFT proximity detection, gate logic, mic volume control
HotkeyMonitor.swift           — Carbon RegisterEventHotKey + flagsState polling
AudioDeviceManager.swift      — CoreAudio device enumeration, volume control
PopoverView.swift             — Menu bar popover UI (lazy rendering)
SetupView.swift               — First-launch setup window (NSWindow)
CalibrateButton.swift         — Two-step calibration wizard
SuperWhisperIntegration.swift — Reads shortcuts from superwhisper UserDefaults
LoginItemManager.swift        — SMAppService for start-at-login
```

### Proximity Detection Algorithm
```
FFT (2048-point, Hann windowed) → frequency band energy:
  Low  (80-300 Hz):   proximity bass boost, strong for close speech
  Mid  (300-3000 Hz): speech intelligibility band
  High (3000-8000 Hz): sibilance, present in close speech

Proximity score = midEnergy + bassBoost + sibilanceBoost
  bassBoost = max(0, lowEnergy - midEnergy + 6) * 1.5
  sibilanceBoost = max(0, highEnergy - midEnergy + 10) * 0.5

Close speech (1ft): strong bass + strong mid + sibilance → high score
Distant TV (12ft): weak bass + moderate mid + no sibilance → low score
```

### Gate State Machine
```
ENGAGE → CLOSED (mic muted)
CLOSED: peek at 1% volume every 150ms
  → onBuffer sees isPeeking, reads one buffer, compensates +40dB
  → if proximityScore >= threshold + hysteresis: OPEN (restore volume)
  → else: re-mute
OPEN: continuous monitoring at full volume
  → if proximityScore >= threshold: reset lastSpeech timer
  → if below threshold for holdTime (200ms): CLOSE (mute)
DISENGAGE → restore saved volume
```

### Hotkey Detection (zero permissions)
```
Combo shortcuts (e.g. Option+Space):
  → Carbon RegisterEventHotKey — event-driven, zero CPU

Modifier-only shortcuts (e.g. Right Option):
  → DispatchSource timer at 4Hz on background queue
  → CGEventSource.flagsState with device-dependent bits (left/right distinction)
```

### Permissions Required
- Microphone only (prompted on first launch via setup window)
- No Accessibility or Input Monitoring needed

## File Locations
- Project: ~/Desktop/WhisperGate/
- Build output: ~/Desktop/WhisperGate/build/WhisperGate.app
- superwhisper prefs: ~/Library/Preferences/com.superduper.superwhisper.plist
- WhisperGate prefs: ~/Library/Preferences/com.whispergate.app.plist

## Next Steps (Priority Order)

### P0: Test & Tune Proximity Detection
1. Test with TV at various volumes
2. Tune bass/mid/high weights for best discrimination
3. Verify CPU usage stays reasonable with FFT
4. May need to adjust hysteresis, hold time, peek interval

### P1: Polish macOS App
1. Test fresh install flow end-to-end
2. Verify calibration accuracy
3. Remove setup Input Monitoring step (not needed)
4. Clean up unused files (generate_icon.swift)
5. Prepare for GitHub

### P2: Windows Version (C# / WinUI3)
Architecture mapping:
- Audio capture: NAudio (NuGet) replaces AudioQueue
- FFT: System.Numerics or MathNet.Numerics replaces vDSP
- Volume control: Windows CoreAudio API (MMDevice) replaces AudioObjectSetPropertyData
- Hotkey detection: Win32 RegisterHotKey replaces Carbon RegisterEventHotKey
- System tray: WinUI3 + NotifyIcon
- Settings storage: read superwhisper Windows preferences (location TBD)
- Build: dotnet build / MSBuild script

Core algorithm (FFT proximity detection, gate state machine) ports directly.
Platform-specific code: audio capture, volume control, hotkey, UI.

### P3: GitHub Release
- macOS code in `macos/` folder
- Windows code in `windows/` folder
- README with build instructions for both platforms
- build.sh (macOS) and build.ps1 or build.bat (Windows)
