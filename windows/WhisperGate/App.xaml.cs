using System;
using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace WhisperGate;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    public NoiseGateEngine Engine { get; private set; } = null!;
    public HotkeyManager Hotkeys { get; private set; } = null!;
    public Settings AppSettings { get; private set; } = null!;

    public static App Instance => (App)Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings = Settings.Load();
        Log($"Settings loaded: PTT=0x{AppSettings.PushToTalkKey:X} ({AppSettings.PushToTalkDisplay}), Rec=0x{AppSettings.ToggleRecordingKey:X} ({AppSettings.ToggleRecordingDisplay}), Threshold={AppSettings.Threshold}");

        if (AppSettings.PushToTalkKey == 0 && AppSettings.ToggleRecordingKey == 0)
        {
            Log("No shortcuts configured, syncing from superwhisper...");
            SuperWhisperIntegration.SyncShortcuts(AppSettings);
            Log($"After sync: PTT=0x{AppSettings.PushToTalkKey:X} ({AppSettings.PushToTalkDisplay}), Rec=0x{AppSettings.ToggleRecordingKey:X} ({AppSettings.ToggleRecordingDisplay})");
        }

        Engine = new NoiseGateEngine(AppSettings);
        Hotkeys = new HotkeyManager(AppSettings, Engine);
        Hotkeys.Register();
        Log("Hotkey polling started");

        // System tray
        Icon? trayIcon = null;
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(icoPath)) trayIcon = new Icon(icoPath);
        }
        catch { }
        trayIcon ??= System.Drawing.SystemIcons.Application;

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WhisperGate - Standby",
            Icon = trayIcon,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();

        // Modern styled context menu
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x40, 0x40)),
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "  Settings..." };
        settingsItem.Click += (_, _) => ShowSettings();
        var syncItem = new System.Windows.Controls.MenuItem { Header = "  Sync from superwhisper" };
        syncItem.Click += (_, _) =>
        {
            SuperWhisperIntegration.SyncShortcuts(AppSettings);
            AppSettings.Save();
            Hotkeys.Unregister();
            Hotkeys.Register();
        };
        var quitItem = new System.Windows.Controls.MenuItem { Header = "  Quit WhisperGate" };
        quitItem.Click += (_, _) =>
        {
            Engine.DisengageGate();
            _trayIcon?.Dispose();
            Shutdown();
        };
        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(syncItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(quitItem);
        _trayIcon.ContextMenu = menu;

        // Hide the main window on startup — tray only
        MainWindow?.Hide();
    }

    public void ShowSettings()
    {
        if (MainWindow == null || !MainWindow.IsLoaded)
        {
            MainWindow = new SettingsWindow();
        }
        MainWindow.Show();
        MainWindow.Activate();
    }

    public void UpdateTrayTooltip(string text)
    {
        if (_trayIcon != null)
            _trayIcon.ToolTipText = text;
    }

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperGate", "debug.log");

    public static void Log(string msg)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogPath)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
