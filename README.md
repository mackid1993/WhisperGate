# WhisperGate

A lightweight noise gate for [superwhisper](https://superwhisper.com) dictation. Filters out background noise (TV, music, conversations) so only your voice gets transcribed. Available for macOS and Windows.

## How It Works

1. **Press your superwhisper hotkey** — WhisperGate activates the noise gate
2. **Start speaking** — your voice is detected and passes through instantly
3. **Stop speaking** — the gate closes after a brief hold, silencing background noise
4. **Release the hotkey (or press Escape)** — mic returns to normal

### Virtual Mic (macOS)

WhisperGate includes an optional virtual audio driver that creates a **"WhisperGate Mic"** input device. When the gate is closed, the virtual mic outputs **true silence** (zero bytes) — completely eliminating STT hallucinations from background noise.

Without the virtual mic, WhisperGate falls back to reducing your system mic volume, which can still leak audio on Mac (volume 0 leaks ~20dB on macOS).

## Features

- Syncs hotkeys directly from superwhisper preferences (Push to Talk + Toggle Recording)
- **Virtual Mic Driver** (macOS) — true silence when gated, no hallucinations
- Threshold slider — set it just above your background noise level
- Gated Volume slider — control mic reduction in volume fallback mode
- Escape key cancels dictation (only intercepted when gate is active)
- Near-zero CPU when idle — mic only active during dictation
- System tray / menu bar icon shows gate state
- Start at login option

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

> **Note:** On Windows, volume 0 is true silence — no virtual mic driver is needed. The volume-based gate works perfectly.

---

## Settings

### Threshold
Controls how loud audio needs to be to open the gate (let your voice through).
- **Lower** (toward -60 dB): less filtering, gate opens more easily
- **Higher** (toward -20 dB): more filtering, only louder speech opens the gate

Set it just above your background noise level.

### Gated Volume (volume fallback mode only)
Controls how much the mic volume is reduced when you're not speaking. Only visible when the virtual mic driver is not installed.
- **0%**: mic fully silenced when gating (most aggressive)
- **30%**: gentle reduction (default)
- **100%**: no reduction at all

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

You only need to do this once. After that, WhisperGate will open normally.

Alternatively, you can bypass Gatekeeper from Terminal:
```bash
xattr -cr /Applications/WhisperGate.app
```

### Windows — SmartScreen Warning

Since WhisperGate is not signed with a code signing certificate, Windows SmartScreen will show a warning when you first run it.

1. Download `WhisperGate-Windows.zip` and unzip it
2. Double-click `WhisperGate.exe` — Windows will show "Windows protected your PC"
3. Click **More info**
4. Click **Run anyway**

You only need to do this once. After that, Windows will remember your choice.

If you prefer, you can right-click the exe > **Properties** > check **Unblock** > **OK** before running.

---

## Disclaimer

WhisperGate is an independent, open-source project. It is not affiliated with, endorsed by, or sponsored by [superwhisper](https://superwhisper.com) or SuperUltra, Inc. "superwhisper" is a trademark of SuperUltra, Inc. All trademarks belong to their respective owners.

WhisperGate reads superwhisper's preferences solely to sync hotkey shortcuts for user convenience. It does not modify, interfere with, or reverse-engineer superwhisper in any way.

## License

MIT
