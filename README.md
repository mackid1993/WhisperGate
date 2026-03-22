# WhisperGate

A lightweight noise gate for [superwhisper](https://superwhisper.com) dictation. Filters out background noise (TV, music, conversations) so only your voice gets transcribed. Available for macOS and Windows.

## How It Works

WhisperGate syncs your superwhisper hotkeys and activates automatically when you dictate:

1. **Press your superwhisper hotkey** — WhisperGate reduces your mic level to filter background noise
2. **Start speaking** — your voice is detected and mic restores to full volume instantly
3. **Stop speaking** — mic level drops again after a brief hold, silencing background noise
4. **Release the hotkey (or press Escape)** — mic returns to normal

No virtual audio devices. No complex routing. Just smart mic volume control timed to your speech.

## Features

- Syncs hotkeys directly from superwhisper preferences (Push to Talk + Toggle Recording)
- Simple threshold slider — set it just above your background noise level
- Gentle volume reduction when gating (30% / ~10dB) — no harsh audio artifacts
- Escape key cancels dictation (matches superwhisper behavior)
- Near-zero CPU when idle — mic only active during dictation
- System tray icon changes color to show gate state (standby / active / gating)
- Start at login option

---

## macOS

### Requirements

- macOS 14 (Sonoma) or later
- Xcode Command Line Tools

### Building

```bash
# Install command line tools (if not already installed)
xcode-select --install

# Build
cd macos
./build.sh

# Run
open build/WhisperGate.app
```

The build script compiles a universal binary (Apple Silicon + Intel), generates the app icon, and signs it ad-hoc.

### Setup

On first launch, WhisperGate will ask for **Microphone** permission and show your synced superwhisper shortcuts.

---

## Windows

### Requirements

- Windows 10 (build 19041) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (download the SDK installer for your architecture)

### Building

```cmd
cd windows
build.bat
```

The build script compiles a self-contained exe with all dependencies included. The output is in `windows\build\WhisperGate.exe`.

### Setup

WhisperGate launches silently to the system tray. Double-click the tray icon or right-click and select **Settings** to open the settings window.

Shortcuts are automatically synced from superwhisper's preferences at `%APPDATA%\com.superwhisper.app\preferences.json`. Click **Sync** in the settings window if you change your superwhisper shortcuts.

---

## Setting the Threshold

Use the threshold slider to control how aggressive the gate is:

- **Lower values** (toward -60 dB): less filtering, gate opens more easily
- **Higher values** (toward -20 dB): more filtering, only louder speech opens the gate

Set it just above your background noise level. If you have a TV on, slide it higher. In a quiet room, slide it lower.

## How the Gate Works

```
Hotkey pressed -> mic volume reduced to 30%
       |
Your voice detected (above threshold) -> mic restored to full volume
       |
You stop speaking (300ms hold) -> mic volume reduced to 30% again
       |
Hotkey released or Escape pressed -> mic fully restored to normal
```

- **Threshold**: user-adjustable, -60 to -20 dB
- **Reduction**: fixed 30% volume (~10 dB drop) — gentle, no choppy artifacts
- **Hold time**: 300ms — keeps gate open during natural pauses between words
- **Detection**: energy-based (RMS level), continuously monitored
- **No muting/unmuting cycles**: just two volume levels (full and reduced)

## Project Structure

```
macos/
  Sources/           — Swift source files
  Resources/         — Info.plist, entitlements
  build.sh           — Build script (requires Xcode CLI tools)
  generate_icon.sh   — App icon generator

windows/
  WhisperGate/       — C# / WPF source files
  build.bat          — Build script (requires .NET 8 SDK)
```

## Disclaimer

WhisperGate is an independent, open-source project. It is not affiliated with, endorsed by, or sponsored by [superwhisper](https://superwhisper.com) or SuperUltra, Inc. "superwhisper" is a trademark of SuperUltra, Inc. All trademarks belong to their respective owners.

WhisperGate reads superwhisper's preferences solely to sync hotkey shortcuts for user convenience. It does not modify, interfere with, or reverse-engineer superwhisper in any way.

## License

MIT
