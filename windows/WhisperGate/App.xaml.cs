using System;
using H.NotifyIcon;
using Microsoft.UI.Xaml;

namespace WhisperGate;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;

    public NoiseGateEngine Engine { get; private set; } = null!;
    public HotkeyManager Hotkeys { get; private set; } = null!;
    public Settings AppSettings { get; private set; } = null!;

    public static App Instance => (App)Current;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppSettings = Settings.Load();

        if (AppSettings.PushToTalkKey == 0 && AppSettings.ToggleRecordingKey == 0)
            SuperWhisperIntegration.SyncShortcuts(AppSettings);

        Engine = new NoiseGateEngine(AppSettings);
        Hotkeys = new HotkeyManager(AppSettings, Engine);
        Hotkeys.Register();

        // Create tray icon
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WhisperGate - Standby",
            Icon = new System.Drawing.Icon("icon.ico"),
        };

        var menu = new H.NotifyIcon.Core.PopupMenu();
        menu.Items.Add(new H.NotifyIcon.Core.PopupMenuItem("Settings...", (_, _) => ShowSettings()));
        menu.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
        menu.Items.Add(new H.NotifyIcon.Core.PopupMenuItem("Sync from superwhisper", (_, _) =>
        {
            SuperWhisperIntegration.SyncShortcuts(AppSettings);
            AppSettings.Save();
            Hotkeys.Unregister();
            Hotkeys.Register();
        }));
        menu.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
        menu.Items.Add(new H.NotifyIcon.Core.PopupMenuItem("Quit", (_, _) =>
        {
            Engine.DisengageGate();
            _trayIcon?.Dispose();
            Exit();
        }));
        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    public void UpdateTrayTooltip(string text)
    {
        if (_trayIcon != null)
            _trayIcon.ToolTipText = text;
    }
}
