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
        if (AppSettings.PushToTalkKey == 0 && AppSettings.ToggleRecordingKey == 0)
            SuperWhisperIntegration.SyncShortcuts(AppSettings);

        Engine = new NoiseGateEngine(AppSettings);
        Hotkeys = new HotkeyManager(AppSettings, Engine);
        Hotkeys.Register();

        // System tray
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WhisperGate - Standby",
            Icon = new Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico")),
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();

        var menu = new System.Windows.Controls.ContextMenu();
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings..." };
        settingsItem.Click += (_, _) => ShowSettings();
        var syncItem = new System.Windows.Controls.MenuItem { Header = "Sync from superwhisper" };
        syncItem.Click += (_, _) =>
        {
            SuperWhisperIntegration.SyncShortcuts(AppSettings);
            AppSettings.Save();
            Hotkeys.Unregister();
            Hotkeys.Register();
        };
        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
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
}
