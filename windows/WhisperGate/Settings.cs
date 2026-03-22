using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace WhisperGate;

class Settings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperGate", "settings.json");

    public Keys PushToTalkKey { get; set; } = Keys.None;
    public Keys PushToTalkModifiers { get; set; } = Keys.None;
    public Keys ToggleRecordingKey { get; set; } = Keys.None;
    public Keys ToggleRecordingModifiers { get; set; } = Keys.None;
    public float Threshold { get; set; } = -40f;
    public bool StartAtLogin { get; set; } = false;

    // Display strings for UI
    public string PushToTalkDisplay { get; set; } = "Not Set";
    public string ToggleRecordingDisplay { get; set; } = "Not Set";

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static Settings Load()
    {
        if (!File.Exists(SettingsPath)) return new Settings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch { return new Settings(); }
    }
}
