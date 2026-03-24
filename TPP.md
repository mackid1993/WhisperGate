# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. Filters background noise so only your voice gets transcribed. macOS and Windows.

## Current State (2026-03-23)

### macOS: v1.1.0 SHIPPED — DO NOT TOUCH
Virtual mic driver works perfectly. True silence via chunk replacement.

### Windows: BROKEN — needs rewrite to system volume approach

## CRITICAL: Windows Must Be Fixed

### What Works on Windows
- Hotkey detection (WH_KEYBOARD_LL)
- superwhisper shortcut sync
- Sleep/wake hook re-registration
- Single instance enforcement
- UI (settings window, tray icon)
- RMS calculation via WaveInEvent (Int16, 48kHz mono, 50ms buffers)
- Finding superwhisper's PID via AudioSessionManager

### What's Broken
The gate logic has been rewritten ~20 times and is currently non-functional. The per-app volume approach (`SimpleAudioVolume` on superwhisper's capture session) was proven to kill ALL capture on the device — both WasapiCapture (-144dB) and WaveInEvent (-96dB) go silent when any session volume is set to 0.

### The Only Approach That Works on Windows
**System volume manipulation** — the original v1.0 approach:
- Gate closed: `device.AudioEndpointVolume.MasterVolumeLevelScalar = savedVolume * reductionFactor`
- Gate open: `device.AudioEndpointVolume.MasterVolumeLevelScalar = savedVolume`
- Detection via WaveInEvent at whatever the current volume is
- `threshold - 6` hysteresis (same as Mac)

This worked in v1.0. All the subsequent changes broke it.

### What the Next Session Must Do
1. **Revert NoiseGateEngine.cs to the simple system volume approach** — no per-app volume, no exclusive mode, no WasapiCapture. Just WaveInEvent + system volume. Match Mac's gate logic exactly.
2. **Remove all dead UI elements** — no True Silence toggle, no Exclusive Mode, no Force Max Volume. Just threshold slider and gated volume slider (minimum 5%).
3. **Add the Sophist-style hard gate as an option** — when enabled, every chunk: `db >= threshold` → full volume, `db < threshold` → gated volume. No hold time, no hysteresis. Simple.
4. **Test thoroughly** before committing.
5. **Clean up Settings.cs** — remove ExclusiveModeEnabled and any other dead settings.

### Reference: Working Mac Gate Logic (NoiseGateEngine.swift)
```swift
if gateIsOpen {
    if db >= cachedThreshold {
        lastSpeechTime = now
    } else if (now - lastSpeechTime) > cachedHoldTime {
        gateIsOpen = false
        // set volume to gated level
    }
} else {
    if db >= cachedThreshold - 6 {
        gateIsOpen = true
        lastSpeechTime = now
        // set volume to saved level
    }
}
```
Key: volume only changes on state transitions. Never per-buffer. Capture is always at whatever volume is set — no separate detection path.

### Reference: Sophist Hard Gate (from technical paper)
```
if rmsDB < threshold:
    output silence (or set volume to gated level)
else:
    pass through (or set volume to full)
```
No state. No hysteresis. No hold time. Per-chunk decision.

## PROVEN FAILURES on Windows — Do NOT Retry
- **Per-app capture volume (SimpleAudioVolume)** — kills ALL capture on the device, not just the target app. Both WasapiCapture and WaveInEvent go silent.
- **WASAPI exclusive mode** — NAudio 2.2.1's IsFormatSupported broken (non-null ppClosestMatch for exclusive). AudioClient.Initialize fails with AUDCLNT_E_UNSUPPORTED_FORMAT for all format/mode combinations. MixFormat rejected. Native format from property store rejected.
- **WasapiCapture for detection** — gets silence when per-app volume is 0. Not independent.
- **Mute toggle (AudioEndpointVolume.Mute)** — affects WaveInEvent capture too.
- **ForceMaxVolume (set 1.0f every buffer)** — causes gate chatter/oscillation.
- **dB compensation formulas** — unreliable, broke level meter display.
- **Pulsed detection (unmute briefly to detect)** — WaveInEvent doesn't recover after volume raised from 0.
- **Three-state gate** — overcomplicated, introduced more bugs.

## Windows Key Files
```
windows/WhisperGate/NoiseGateEngine.cs    — NEEDS REWRITE
windows/WhisperGate/Settings.cs           — Remove dead settings
windows/WhisperGate/SettingsWindow.xaml   — Remove dead UI elements
windows/WhisperGate/SettingsWindow.xaml.cs — Remove dead handlers
windows/WhisperGate/App.xaml.cs           — Sleep/wake + single instance (keep)
windows/WhisperGate/HotkeyManager.cs      — Working (keep)
```

## macOS — COMPLETE, DO NOT MODIFY
Virtual mic driver at `/Library/Audio/Plug-Ins/HAL/WhisperGateAudio.driver`. IPC via `/tmp/whispergate_audio.buf`. Zero CPU. Tested on multiple Macs.

## Build
- macOS: `cd macos && ./build.sh`
- Windows: `cd windows && build.bat`

## Repository
https://github.com/mackid1993/WhisperGate
