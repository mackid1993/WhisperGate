using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WhisperGate;

public static class SuperWhisperIntegration
{
    private static readonly string PrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "com.superwhisper.app", "preferences.json");

    public static bool IsInstalled() => File.Exists(PrefsPath);

    public static void SyncShortcuts(Settings settings)
    {
        if (!IsInstalled()) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(PrefsPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("pushToTalkShortcut", out var ptt))
            {
                var str = ptt.GetString();
                if (!string.IsNullOrEmpty(str))
                {
                    var (vk, mods) = ParseShortcut(str);
                    settings.PushToTalkKey = vk;
                    settings.PushToTalkModifiers = mods;
                    settings.PushToTalkDisplay = str;
                }
            }
            if (root.TryGetProperty("toggleRecordingShortcut", out var rec))
            {
                var str = rec.GetString();
                if (!string.IsNullOrEmpty(str))
                {
                    var (vk, mods) = ParseShortcut(str);
                    settings.ToggleRecordingKey = vk;
                    settings.ToggleRecordingModifiers = mods;
                    settings.ToggleRecordingDisplay = str;
                }
            }
            settings.Save();
        }
        catch { }
    }

    private const int MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;

    // Comprehensive mapping from superwhisper/web key names to Windows VK codes
    private static readonly Dictionary<string, int> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modifiers (left/right specific)
        ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3,
        ["ShiftLeft"] = 0xA0, ["ShiftRight"] = 0xA1,
        ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5,
        ["MetaLeft"] = 0x5B, ["MetaRight"] = 0x5C,

        // Common keys
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D,
        ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E,
        ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["ArrowUp"] = 0x26, ["ArrowDown"] = 0x28,
        ["ArrowLeft"] = 0x25, ["ArrowRight"] = 0x27,
        ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,

        // Function keys
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,

        // Numpad
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62,
        ["Numpad3"] = 0x63, ["Numpad4"] = 0x64, ["Numpad5"] = 0x65,
        ["Numpad6"] = 0x66, ["Numpad7"] = 0x67, ["Numpad8"] = 0x68,
        ["Numpad9"] = 0x69, ["NumpadMultiply"] = 0x6A, ["NumpadAdd"] = 0x6B,
        ["NumpadSubtract"] = 0x6D, ["NumpadDecimal"] = 0x6E, ["NumpadDivide"] = 0x6F,
        ["NumpadEnter"] = 0x0D,

        // Punctuation
        ["Semicolon"] = 0xBA, ["Equal"] = 0xBB, ["Comma"] = 0xBC,
        ["Minus"] = 0xBD, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["Backquote"] = 0xC0, ["BracketLeft"] = 0xDB, ["Backslash"] = 0xDC,
        ["BracketRight"] = 0xDD, ["Quote"] = 0xDE,
    };

    private static (int vk, int mods) ParseShortcut(string shortcut)
    {
        var parts = shortcut.Split('+');
        int vk = 0, mods = 0;

        foreach (var part in parts)
        {
            var p = part.Trim();

            // Look up in key map first (covers all specific keys including ControlLeft etc)
            if (KeyMap.TryGetValue(p, out int mapped))
            {
                vk = mapped;
            }

            // Set modifier flags (for both specific and generic modifier names)
            if (p.Contains("Control", StringComparison.OrdinalIgnoreCase)) mods |= MOD_CONTROL;
            else if (p.Contains("Shift", StringComparison.OrdinalIgnoreCase)) mods |= MOD_SHIFT;
            else if (p.Contains("Alt", StringComparison.OrdinalIgnoreCase)) mods |= MOD_ALT;
            else if (p.Contains("Meta", StringComparison.OrdinalIgnoreCase)) { /* no Win mod for RegisterHotKey */ }

            if (vk != 0) continue;

            // "KeyA" through "KeyZ" → VK is just the uppercase letter
            if (p.StartsWith("Key") && p.Length == 4 && char.IsLetter(p[3]))
            {
                vk = char.ToUpper(p[3]);
                continue;
            }

            // "Digit0" through "Digit9"
            if (p.StartsWith("Digit") && p.Length == 6 && char.IsDigit(p[5]))
            {
                vk = p[5]; // VK for 0-9 is the ASCII value
                continue;
            }

            // Single letter
            if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
            {
                vk = char.ToUpper(p[0]);
                continue;
            }

        }

        return (vk, mods);
    }
}
