using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace WhisperGate;

static class SuperWhisperIntegration
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
            var json = File.ReadAllText(PrefsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("pushToTalkShortcut", out var ptt))
            {
                var str = ptt.GetString();
                if (!string.IsNullOrEmpty(str))
                {
                    var (key, mods) = ParseShortcut(str);
                    settings.PushToTalkKey = key;
                    settings.PushToTalkModifiers = mods;
                    settings.PushToTalkDisplay = str;
                }
            }

            if (root.TryGetProperty("toggleRecordingShortcut", out var rec))
            {
                var str = rec.GetString();
                if (!string.IsNullOrEmpty(str))
                {
                    var (key, mods) = ParseShortcut(str);
                    settings.ToggleRecordingKey = key;
                    settings.ToggleRecordingModifiers = mods;
                    settings.ToggleRecordingDisplay = str;
                }
            }
        }
        catch { }
    }

    /// Parse superwhisper shortcut strings like "Control+Shift+Tab" or "ControlRight"
    private static (Keys key, Keys modifiers) ParseShortcut(string shortcut)
    {
        var parts = shortcut.Split('+');
        Keys key = Keys.None;
        Keys mods = Keys.None;

        foreach (var part in parts)
        {
            var p = part.Trim();
            switch (p)
            {
                // Modifiers
                case "Control": case "ControlLeft": mods |= Keys.Control; break;
                case "ControlRight": key = Keys.RControlKey; mods |= Keys.Control; break;
                case "Shift": case "ShiftLeft": mods |= Keys.Shift; break;
                case "ShiftRight": key = Keys.RShiftKey; mods |= Keys.Shift; break;
                case "Alt": case "AltLeft": mods |= Keys.Alt; break;
                case "AltRight": key = Keys.RMenu; mods |= Keys.Alt; break;
                case "Meta": case "MetaLeft": case "MetaRight": mods |= Keys.LWin; break;

                // Common keys
                case "Tab": key = Keys.Tab; break;
                case "Space": key = Keys.Space; break;
                case "Escape": key = Keys.Escape; break;
                case "Enter": key = Keys.Enter; break;
                case "Backspace": key = Keys.Back; break;
                case "Delete": key = Keys.Delete; break;

                // Letters
                default:
                    if (p.StartsWith("Key") && p.Length == 4)
                        key = (Keys)Enum.Parse(typeof(Keys), p[3..]);
                    else if (p.Length == 1 && char.IsLetter(p[0]))
                        key = (Keys)Enum.Parse(typeof(Keys), p.ToUpper());
                    else if (Enum.TryParse<Keys>(p, true, out var k))
                        key = k;
                    break;
            }
        }

        return (key, mods);
    }
}
