# WhisperGate

A lightweight macOS menu bar app that acts as a noise gate for [superwhisper](https://superwhisper.com) dictation. Filters out background noise (TV, music, conversations) so only your voice gets transcribed.

## How It Works

WhisperGate syncs your superwhisper hotkeys and activates automatically when you dictate:

1. **Press your superwhisper hotkey** — WhisperGate reduces your mic level to filter background noise
2. **Start speaking** — your voice is detected and mic restores to full volume instantly
3. **Stop speaking** — mic level drops again within milliseconds, silencing background noise
4. **Release the hotkey** — mic returns to normal

No virtual audio devices. No complex routing. Just smart mic volume control timed to your speech.

## Features

- Syncs hotkeys directly from superwhisper preferences (Push to Talk + Toggle Recording)
- Two-step calibration wizard: measures your room noise, then your voice, auto-sets the threshold
- Hardware mute + volume reduction for maximum noise suppression
- Escape key cancels dictation (matches superwhisper behavior)
- Zero permissions needed for hotkey detection (uses Carbon RegisterEventHotKey + CGEventSource polling)
- Only requires Microphone permission
- Near-zero CPU when idle — mic only active during dictation
- Universal binary (Apple Silicon + Intel)

## Building

Requires only Xcode Command Line Tools. No Xcode project, no third-party dependencies.

```bash
# Install command line tools (if not already installed)
xcode-select --install

# Build
cd macos
./build.sh

# Run
open build/WhisperGate.app
```

The build script compiles a universal binary (arm64 + x86_64), generates the app icon, and signs it ad-hoc. That's it.

## Setup

On first launch, WhisperGate will:

1. Ask for **Microphone** permission — needed to monitor audio levels
2. Show your synced **superwhisper shortcuts** — detected automatically from superwhisper's preferences
3. Walk you through **calibration** — measures your room noise and voice to set the optimal threshold

### Calibration

Click **Calibrate** in the popover:

1. **Step 1**: Let your background noise play (TV, fan, etc). Don't speak. Click "Done" when ready.
2. **Step 2**: Keep the noise playing. Speak at your normal dictation volume over it. Click "Done" when ready.

WhisperGate sets the threshold just above your noise level so only your voice opens the gate. You can also adjust the threshold slider manually.

## Recommended: Enable Voice Isolation

For the best results with superwhisper, enable macOS Voice Isolation:

1. Open **System Settings > Sound**
2. Under **Microphone Mode**, select **Voice Isolation**
3. In superwhisper, make sure your input device is set to your Mac's built-in microphone

Voice Isolation uses Apple's neural engine to separate your voice from background noise at the hardware level. Combined with WhisperGate's volume gating, this gives you extremely clean dictation even in noisy environments.

Note: Voice Isolation is available on Macs with Apple Silicon (M1 or later).

## How the Gate Works

```
Hotkey pressed → mic volume reduced (noise suppressed)
    ↓
Your voice detected (above threshold) → mic restored to full volume
    ↓
You stop speaking (75ms silence) → mic volume reduced again
    ↓
Hotkey released → mic fully restored to normal
```

The gate uses energy-based detection with automatic calibration:

- **Noise measurement**: 99th percentile of room noise level
- **Threshold**: noise level + 15 dB (aggressive, catches loud TV)
- **Reduction target**: pushes noise down to -65 dB (below transcription threshold)
- **Hold time**: 75ms (prevents clipping between words)
- **No muting/unmuting cycles**: just two volume levels (full and reduced), continuously monitored

## Project Structure

```
macos/
  Sources/
    WhisperGateApp.swift          — App entry, MenuBarExtra
    AppState.swift                — State management, UserDefaults
    NoiseGateEngine.swift         — AudioQueue capture, gate logic, volume control
    HotkeyMonitor.swift           — Carbon hotkeys + modifier polling
    AudioDeviceManager.swift      — CoreAudio device enumeration
    PopoverView.swift             — Menu bar popover UI
    SetupView.swift               — First-launch setup window
    CalibrateButton.swift         — Calibration wizard
    SuperWhisperIntegration.swift — Reads superwhisper preferences
    VoiceProfile.swift            — Voice profile types (for future use)
    LoginItemManager.swift        — Start at login
  Resources/
    Info.plist
    WhisperGate.entitlements
  build.sh                        — Build script
  generate_icon.sh                — Icon generator
```

## Platforms

- **macOS** (14+) — available now
- **Windows** — planned (WinUI3 + system tray)

## License

MIT
