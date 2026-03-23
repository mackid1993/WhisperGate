# WhisperGate v1.1.0 — Virtual Mic Driver

## What's New

### Virtual Mic (macOS)

WhisperGate now includes an optional virtual audio driver that creates a **"WhisperGate Mic"** input device on your system. When the noise gate is active and you're not speaking, the virtual mic outputs **true silence** — completely eliminating STT hallucinations from background noise.

**How it works:**
- WhisperGate captures audio from your real microphone for voice detection
- When speech is detected, the audio passes through to the virtual mic at full quality
- When you stop speaking, the virtual mic outputs pure silence (zero bytes)
- superwhisper (or any app) reads from "WhisperGate Mic" and gets clean audio with no noise leakage

**Setup:**
1. Launch WhisperGate — on first launch, you'll be prompted to install the virtual mic driver (requires admin password)
2. In superwhisper, select **"WhisperGate Mic"** as your input device
3. That's it — the gate now provides true silence instead of reduced volume

**Technical details:**
- The driver is a lightweight AudioServerPlugin (HAL plugin) installed to `/Library/Audio/Plug-Ins/HAL/`
- IPC between the app and driver uses a memory-mapped file at `/tmp/whispergate_audio.buf`
- The driver can be added or removed at any time from the Settings section in the WhisperGate popover
- When WhisperGate isn't running, the virtual mic outputs silence
- Zero CPU overhead — the driver uses the same proven timing patterns as BlackHole

### Escape Key Fix (macOS + Windows)

The Escape key is now only intercepted when the noise gate is actively engaged. Previously, it was registered as a global hotkey at all times, stealing Escape from every app.

## Upgrade Notes

- **macOS:** The virtual mic driver is optional. Without it, WhisperGate falls back to the original volume-based gating. You can toggle it on/off in Settings at any time.
- **Windows:** No changes to the gating mechanism. The Escape key fix applies.
- The "Gated Volume" slider still works when the virtual mic is not installed (volume fallback mode).

## Known Limitations

- The virtual mic driver requires admin privileges to install/remove (it's a system-level audio plugin)
- On first install, coreaudiod restarts which briefly interrupts all audio
- The driver is ad-hoc signed — macOS may show a security warning on some systems
- `/tmp/whispergate_audio.buf` is recreated on each app launch (cleared on reboot, which is fine)
