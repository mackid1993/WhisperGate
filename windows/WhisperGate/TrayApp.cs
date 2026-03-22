using System;
using System.Drawing;
using System.Windows.Forms;

namespace WhisperGate;

sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly NoiseGateEngine _engine;
    private readonly HotkeyManager _hotkeys;
    private readonly SettingsForm _settingsForm;
    private readonly Settings _settings;

    public TrayApp()
    {
        _settings = Settings.Load();

        // Sync from superwhisper if no shortcuts configured
        if (_settings.PushToTalkKey == Keys.None && _settings.ToggleRecordingKey == Keys.None)
        {
            SuperWhisperIntegration.SyncShortcuts(_settings);
        }

        _engine = new NoiseGateEngine(_settings);
        _settingsForm = new SettingsForm(_settings, this);

        // System tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WhisperGate - Standby",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        // Global hotkeys
        _hotkeys = new HotkeyManager(_settings, _engine, this);
        _hotkeys.Register();

        UpdateIcon(false, true);
    }

    public void UpdateIcon(bool engaged, bool gateOpen)
    {
        if (engaged && !gateOpen)
            _trayIcon.Text = "WhisperGate - Noise Reduced";
        else if (engaged)
            _trayIcon.Text = "WhisperGate - Full Volume";
        else
            _trayIcon.Text = "WhisperGate - Standby";
    }

    public void RefreshHotkeys()
    {
        _hotkeys.Unregister();
        _hotkeys.Register();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sync from superwhisper", null, (_, _) =>
        {
            SuperWhisperIntegration.SyncShortcuts(_settings);
            _settings.Save();
            RefreshHotkeys();
            _settingsForm.RefreshDisplay();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            _engine.DisengageGate();
            _trayIcon.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    private void ShowSettings()
    {
        if (_settingsForm.Visible)
            _settingsForm.BringToFront();
        else
            _settingsForm.Show();
    }

    public void Dispose()
    {
        _engine.DisengageGate();
        _hotkeys.Unregister();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _settingsForm.Dispose();
    }
}
