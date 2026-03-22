# WhisperGate

A lightweight noise gate for [superwhisper](https://superwhisper.com) dictation. Filters out background noise (TV, music, conversations) so only your voice gets transcribed. Available for macOS and Windows.

## How It Works

1. **Press your superwhisper hotkey** — WhisperGate reduces your mic level to filter background noise
2. **Start speaking** — your voice is detected and mic restores to full volume instantly
3. **Stop speaking** — mic level drops again after a brief hold, silencing background noise
4. **Release the hotkey (or press Escape)** — mic returns to normal

No virtual audio devices. No complex routing. Just smart mic volume control timed to your speech.

## Features

- Syncs hotkeys directly from superwhisper preferences (Push to Talk + Toggle Recording)
- Threshold slider — set it just above your background noise level
- Gated Volume slider — control how much to reduce the mic when not speaking (0% = silent, 100% = no reduction)
- Escape key cancels dictation (matches superwhisper behavior)
- Near-zero CPU when idle — mic only active during dictation
- System tray icon changes color to show gate state
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

On first launch, WhisperGate will ask for **Microphone** permission and show your synced superwhisper shortcuts.

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

### Threshold
Controls how loud audio needs to be to open the gate (let your voice through).
- **Lower** (toward -60 dB): less filtering, gate opens more easily
- **Higher** (toward -20 dB): more filtering, only louder speech opens the gate

Set it just above your background noise level.

### Gated Volume
Controls how much the mic volume is reduced when you're not speaking.
- **0%**: mic fully silenced when gating (most aggressive)
- **30%**: gentle reduction (default)
- **100%**: no reduction at all

If background noise still leaks through, lower this value.

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
