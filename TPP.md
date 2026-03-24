# WhisperGate - Technical Project Plan

## Project Overview
WhisperGate is a lightweight noise gate for superwhisper dictation. Filters background noise so only your voice gets transcribed. macOS and Windows.

## Current State (2026-03-23)

### Status: macOS v1.1.0 SHIPPED, Windows BROKEN — needs fixes

**macOS: DONE.** Virtual mic driver works. Do not touch Mac code.

**Windows: Two open issues.**
1. WASAPI exclusive mode fails on all format/init combinations
2. Gate open threshold compensation is unreliable at low gated volumes

## CRITICAL: Windows Issues

### Issue 1: WASAPI Exclusive Mode Fails

**Goal:** True silence when gated (like Mac's virtual mic) by grabbing the mic in WASAPI exclusive mode — other apps (superwhisper) get silence.

**What happens:** `AudioClient.Initialize` returns `0x88890008` (`AUDCLNT_E_UNSUPPORTED_FORMAT`) for every format tried. The brute-force approach (8 formats × 4 init strategies = 32 attempts) all fail.

**Root cause confirmed:** NAudio 2.2.1's `AudioClient.IsFormatSupported` is broken for exclusive mode — passes non-null `ppClosestMatch` which violates WASAPI spec. This was fixed in NAudio PR #1122 but not released in 2.2.1.

**What to try next (in order):**
1. Read `PKEY_AudioEngine_DeviceFormat` from the device property store (`PropertyKey("f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0)`) — this is the device's native format, guaranteed to work for exclusive mode
2. Call `IAudioClient::IsFormatSupported` via raw COM vtable with `IntPtr.Zero` for `ppClosestMatch`, bypassing NAudio's broken wrapper
3. Try `WaveFormatExtensible` with correct `SubFormat` GUIDs (`KSDATAFORMAT_SUBTYPE_PCM`, `KSDATAFORMAT_SUBTYPE_IEEE_FLOAT`) — some drivers only accept extensible format
4. Handle `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` (0x88890019) by getting aligned buffer size and retrying

**Reference code from research agent (saved in conversation context):**
- `WasapiExclusiveFix.IsFormatSupportedExclusive()` — raw COM vtable call
- `WasapiExclusiveFix.GetDeviceNativeFormat()` — reads PKEY_AudioEngine_DeviceFormat
- `WasapiExclusiveFix.InitializeExclusiveCapture()` — direct Initialize with error handling

**Current code:** `windows/WhisperGate/NoiseGateEngine.cs` — `StartExclusiveCapture()` uses raw `AudioClient` (not NAudio's `WasapiCapture`). Tries 32 format×mode combos, all fail.

### Issue 2: Gate Open Threshold

**Goal:** Gate should open when user speaks, regardless of gated volume setting.

**Problem:** On Windows, capture level drops with system volume. At low gated volumes (5-20%), captured speech is too quiet to cross `threshold - 6` hysteresis.

**Why Mac doesn't have this:** Mac virtual mic captures at full volume always. The threshold comparison is against full-volume audio. On Windows without exclusive mode, capture IS at the gated volume.

**Current formula:** `openThreshold = threshold + 20*log10(reductionFactor) - 8`
This should work mathematically but user reports it's still unreliable.

**IMPORTANT RULE:** Match Mac's gate logic EXACTLY. Do NOT add per-buffer volume forcing, compensation formulas, or extra features. The Mac code sets volume on state transitions only:
- Gate opens → `SetVolume(savedVolume)` (or 1.0f)
- Gate closes → `SetVolume(reductionFactor)`
- Never touch volume during steady state

### Failed Windows Approaches (Do NOT Retry)
- **NAudio `WasapiCapture` with `ShareMode = Exclusive`** — NAudio passes `AutoConvertPcm|SrcDefaultQuality` flags which WASAPI rejects in exclusive mode. NAudio 2.2.1 doesn't have the fix.
- **Subclassing `WasapiCapture` to override `GetAudioClientStreamFlags`** — Method not virtual in 2.2.1.
- **`WaveInEvent` capture with mute toggle** — Mute affects WaveInEvent too, capture goes deaf.
- **Volume 0 with pulsed detection** — WaveInEvent doesn't recover after volume raised from 0.
- **ForceMaxVolume (set 1.0f every buffer)** — Causes gate chatter. Volume must only change on transitions.
- **dB compensation on LatestDB** — Broke the level meter display. Only compensate the threshold comparison.
- **NAudio `IsFormatSupported` for exclusive mode** — Broken in 2.2.1, always returns false.

## macOS — COMPLETE, DO NOT MODIFY

### Virtual Mic Driver
- HAL plugin at `/Library/Audio/Plug-Ins/HAL/WhisperGateAudio.driver`
- IPC via mmap'd file at `/tmp/whispergate_audio.buf` (permissions `0o666`)
- `kDevice_RingBufferSize = 16384`, `kRing_Buffer_Frame_Size = 65536`
- coreaudiod sandbox blocks `shm_open` and `~/` access — use `/tmp/`
- `CanBeDefaultDevice = 0` — only selectable manually in superwhisper
- Tested on multiple Macs, zero CPU overhead

### Key Files
```
macos/HALPlugin/WhisperGateDriver.c  — AudioServerPlugin (~500 lines C)
macos/Sources/NoiseGateEngine.swift  — Gate logic + ring buffer writing
macos/Sources/SharedRingBuffer.swift — mmap'd file IPC
macos/Sources/DriverInstaller.swift  — Install/uninstall with admin
macos/Sources/AppState.swift         — virtualMicEnabled toggle
macos/build.sh                       — Builds app + HAL plugin
```

## Windows Key Files
```
windows/WhisperGate/NoiseGateEngine.cs    — Gate logic + exclusive mode
windows/WhisperGate/Settings.cs           — ExclusiveModeEnabled toggle
windows/WhisperGate/SettingsWindow.xaml   — UI with exclusive mode toggle + error display
windows/WhisperGate/App.xaml.cs           — Sleep/wake hook re-registration
windows/WhisperGate/HotkeyManager.cs      — WH_KEYBOARD_LL hook
```

## Build
- macOS: `cd macos && ./build.sh`
- Windows: `cd windows && build.bat`

## Repository
https://github.com/mackid1993/WhisperGate
