using System;
using System.IO;
using System.Text.Json;

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
                    App.Log($"Synced PTT: '{str}' → vk=0x{vk:X} mods=0x{mods:X}");
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
                    App.Log($"Synced Rec: '{str}' → vk=0x{vk:X} mods=0x{mods:X}");
                }
            }
            settings.Save();
        }
        catch { }
    }

    private const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
    private const int VK_BACK = 0x08, VK_DELETE = 0x2E;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;

    private static (int vk, int mods) ParseShortcut(string shortcut)
    {
        var parts = shortcut.Split('+');
        int vk = 0, mods = 0;
        foreach (var part in parts)
        {
            switch (part.Trim())
            {
                case "Control": mods |= MOD_CONTROL; break;
                case "ControlLeft": vk = VK_LCONTROL; mods |= MOD_CONTROL; break;
                case "ControlRight": vk = VK_RCONTROL; mods |= MOD_CONTROL; break;
                case "Shift": mods |= MOD_SHIFT; break;
                case "ShiftLeft": vk = VK_LSHIFT; mods |= MOD_SHIFT; break;
                case "ShiftRight": vk = VK_RSHIFT; mods |= MOD_SHIFT; break;
                case "Alt": mods |= MOD_ALT; break;
                case "AltLeft": vk = VK_LMENU; mods |= MOD_ALT; break;
                case "AltRight": vk = VK_RMENU; mods |= MOD_ALT; break;
                case "Tab": vk = VK_TAB; break;
                case "Space": vk = VK_SPACE; break;
                case "Escape": vk = VK_ESCAPE; break;
                case "Enter": vk = VK_RETURN; break;
                case "Backspace": vk = VK_BACK; break;
                case "Delete": vk = VK_DELETE; break;
                default:
                    var p = part.Trim();
                    if (p.StartsWith("Key") && p.Length == 4) vk = char.ToUpper(p[3]);
                    else if (p.Length == 1 && char.IsLetter(p[0])) vk = char.ToUpper(p[0]);
                    break;
            }
        }
        return (vk, mods);
    }
}
