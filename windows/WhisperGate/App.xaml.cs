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
        Log("=== WhisperGate v2 ===");
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

        // Tray icon state timer (updates icon color based on gate state)
        var iconTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        iconTimer.Tick += (_, _) => UpdateTrayState(Engine.IsEngaged, Engine.IsGateOpen);
        iconTimer.Start();

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
        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit WhisperGate" };
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

    private Icon? _iconStandby;
    private Icon? _iconActive;
    private Icon? _iconGating;

    public void UpdateTrayState(bool engaged, bool gateOpen)
    {
        if (_trayIcon == null) return;

        if (engaged && gateOpen)
        {
            _trayIcon.ToolTipText = "WhisperGate - Active";
            _trayIcon.Icon = _iconActive ??= MakeTrayIcon(System.Drawing.Color.FromArgb(108, 203, 95)); // green
        }
        else if (engaged)
        {
            _trayIcon.ToolTipText = "WhisperGate - Noise Reduced";
            _trayIcon.Icon = _iconGating ??= MakeTrayIcon(System.Drawing.Color.FromArgb(252, 185, 56)); // amber
        }
        else
        {
            _trayIcon.ToolTipText = "WhisperGate - Standby";
            _trayIcon.Icon = _iconStandby ??= MakeTrayIcon(System.Drawing.Color.FromArgb(150, 150, 150)); // gray
        }
    }

    private static Icon MakeTrayIcon(System.Drawing.Color color)
    {
        var bmp = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        using var brush = new System.Drawing.SolidBrush(color);
        using var pen = new System.Drawing.Pen(color, 1.5f);

        // Mic body
        g.FillEllipse(brush, 5, 2, 6, 8);

        // Mic arc
        g.DrawArc(pen, 3, 4, 10, 10, 0, 180);

        // Stand
        g.DrawLine(pen, 8, 14, 8, 12);
        g.DrawLine(pen, 5, 14, 11, 14);

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }
}
