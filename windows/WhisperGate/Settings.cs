using System;
using System.IO;
using System.Text.Json;

namespace WhisperGate;

public class Settings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperGate", "settings.json");

    public int PushToTalkKey { get; set; }
    public int PushToTalkModifiers { get; set; }
    public int ToggleRecordingKey { get; set; }
    public int ToggleRecordingModifiers { get; set; }
    public float Threshold { get; set; } = -40f;
    public float ReductionPercent { get; set; } = 30f;
    public bool ExclusiveModeEnabled { get; set; }
    public bool StartAtLogin { get; set; }
    public string PushToTalkDisplay { get; set; } = "Not Set";
    public string ToggleRecordingDisplay { get; set; } = "Not Set";

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Settings Load()
    {
        if (!File.Exists(SettingsPath)) return new Settings();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(); }
        catch { return new Settings(); }
    }
}
