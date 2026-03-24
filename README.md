# WhisperGate

A lightweight noise gate for [superwhisper](https://superwhisper.com) dictation. Filters out background noise (TV, music, conversations) so only your voice gets transcribed. Available for macOS and Windows.

**This is something I built primarily for myself, so I won't be accepting feature requests or issues. If it doesn't work for you, I'm sorry. It's not intended to. It's intended to work for me. I'm just providing it to help the community if it works for some people. I provided binaries as a courtesy, but please consider building from source. Please also note that no support will be provided. This is really built for me.**

## How It Works

1. **Press your superwhisper hotkey** — WhisperGate activates the noise gate
2. **Start speaking** — your voice is detected and passes through instantly
3. **Stop speaking** — the gate closes after a brief hold, silencing background noise
4. **Release the hotkey (or press Escape)** — mic returns to normal

## Platform Architecture

WhisperGate uses different architectures on macOS and Windows due to fundamental platform differences in audio APIs.

### macOS — Virtual Mic Driver (True Silence)

WhisperGate includes a virtual audio driver that creates a **"WhisperGate Mic"** input device. This is a **feedforward** design — the detection path is completely independent from the output:

- WhisperGate captures from the real mic at full volume (always)
- Gate open → raw audio passes to the virtual mic
- Gate closed → virtual mic outputs pure silence (zero bytes)
- superwhisper reads from "WhisperGate Mic" and gets perfectly clean audio

This is possible because Apple provides `AudioServerPlugin` — a user-space API for creating virtual audio devices. No kernel code, no driver signing certificates required.

### Windows — Volume Control with Compensated Threshold

Windows does not have an equivalent to macOS's AudioServerPlugin. Virtual audio devices on Windows require kernel-mode drivers with code signing certificates ($200+/year). Every alternative was explored and ruled out:

- **WASAPI Exclusive Mode** — NAudio's implementation is broken for exclusive capture
- **Per-app capture volume** — silences ALL capture on the device, not just the target app
- **Audio Processing Objects** — per-device, not per-app; also silences our detection
- **Endpoint mute** — same result as per-app volume

Instead, WhisperGate uses **system volume control** — a feedback topology where the detection capture is affected by the gate's own volume changes. This requires:

- A configurable **gated volume** floor (default 20%) so the mic still picks up enough signal for detection
- An automatically **compensated open threshold** that accounts for the volume reduction
- **Instant opening** (no delay) so speech comes through immediately
- A **smoothed close** transition to avoid harsh audio cutoffs
- A **debounce** on closing to prevent oscillation from the volume feedback loop

When you speak, the mic volume jumps to 100%. When you stop, it drops to the gated volume. Your original volume is restored when you stop dictating.

## Features

- Syncs hotkeys directly from superwhisper preferences (Push to Talk + Toggle Recording)
- **Virtual Mic Driver** (macOS) — true silence when gated, no hallucinations
- **Smart Volume Gate** (Windows) — compensated threshold, smooth transitions
- Threshold slider — set it just above your background noise level
- Gated Volume slider (Windows) — tune for your mic's sensitivity
- Escape key cancels dictation (only intercepted when gate is active)
- Near-zero CPU when idle — mic only active during dictation
- System tray / menu bar icon shows gate state
- Start at login option
- Single instance enforcement (Windows)
- Sleep/wake hotkey re-registration (Windows)

---

## macOS

### Requirements

- macOS 14 (Sonoma) or later
- Xcode Command Line Tools

### How to Build

1. Open Terminal
2. Install Xcode Command Line Tools (if you haven't already):
   ```bash
   xcode-select --install
   ```
3. Clone the repo:
   ```bash
   git clone https://github.com/mackid1993/WhisperGate.git
   cd WhisperGate
   ```
4. Build:
   ```bash
   cd macos
   ./build.sh
   ```
5. Run:
   ```bash
   open build/WhisperGate.app
   ```

Or copy it to your Applications folder:
```bash
cp -R build/WhisperGate.app /Applications/
```

### First Launch

On first launch, WhisperGate will:
1. Ask for **Microphone** permission
2. Show your synced superwhisper shortcuts
3. Offer to install the **Virtual Mic Driver** (recommended)

### Virtual Mic Setup

When you enable the virtual mic driver:
- You'll be prompted for your **admin password** (the driver installs to `/Library/Audio/Plug-Ins/HAL/`)
- Core Audio restarts briefly (may cause a momentary system hang)
- **"WhisperGate Mic"** appears as an input device

After installation, open superwhisper and select **"WhisperGate Mic"** as your input device. You can add or remove the driver at any time from Settings.

---

## Windows

### Requirements

- Windows 10 (build 19041) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) — download the **SDK** installer (not Runtime) for your architecture (x64 or ARM64)

### How to Build

1. Install the .NET 8 SDK from the link above
2. Clone the repo:
   ```cmd
   git clone https://github.com/mackid1993/WhisperGate.git
   cd WhisperGate
   ```
3. Build:
   ```cmd
   cd windows
   build.bat
   ```
4. Run `windows\build\WhisperGate.exe`

WhisperGate launches silently to the system tray. Double-click the tray icon or right-click > **Settings** to configure.

Shortcuts are automatically synced from superwhisper's preferences. Click **Sync** in settings if you change your superwhisper shortcuts.

---

## Settings

### Threshold (both platforms)
Controls when audio passes through. Start at -40 dB and adjust:
- If the gate **doesn't open** when you speak → move left (more sensitive)
- If **background noise leaks** through → move right (less sensitive)

### Gated Volume (Windows only, 5% to 50%)
Mic volume while you're not speaking. Lower = less background noise but the gate may struggle to detect your voice. Higher = easier detection but more noise leaks. **Start at 20%** — increase if the gate doesn't reopen when you speak, decrease if you hear too much noise.

### Virtual Mic Driver (macOS only)
Toggle in Settings to install or remove the virtual audio driver. When installed, the Gated Volume slider is hidden — the gate uses chunk replacement (true silence) instead of volume reduction.

---

## Installing Pre-built Binaries

### macOS — Gatekeeper Warning

Since WhisperGate is not signed with an Apple Developer certificate, macOS will show a warning when you first try to open it.

1. Download `WhisperGate-macOS.zip` and unzip it
2. Move `WhisperGate.app` to your Applications folder
3. Double-click to open — macOS will say it "can't be opened because Apple cannot check it for malicious software"
4. Open **System Settings > Privacy & Security**
5. Scroll down — you'll see a message about WhisperGate being blocked
6. Click **Open Anyway**
7. macOS will ask one more time — click **Open**

You only need to do this once. Alternatively, bypass from Terminal:
```bash
xattr -cr /Applications/WhisperGate.app
```

### Windows — SmartScreen Warning

Since WhisperGate is not signed with a code signing certificate, Windows SmartScreen will show a warning when you first run it.

1. Download `WhisperGate-Windows.zip` and unzip it
2. Double-click `WhisperGate.exe` — Windows will show "Windows protected your PC"
3. Click **More info**
4. Click **Run anyway**

You only need to do this once. Alternatively, right-click the exe > **Properties** > check **Unblock** > **OK** before running.

---

## Disclaimer

WhisperGate is an independent, open-source project. It is not affiliated with, endorsed by, or sponsored by [superwhisper](https://superwhisper.com) or SuperUltra, Inc. "superwhisper" is a trademark of SuperUltra, Inc. All trademarks belong to their respective owners.

WhisperGate reads superwhisper's preferences solely to sync hotkey shortcuts for user convenience. It does not modify, interfere with, or reverse-engineer superwhisper in any way.

## License

MIT
