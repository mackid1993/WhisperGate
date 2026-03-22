using System;
using System.Windows;
using System.Windows.Threading;

namespace WhisperGate;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly DispatcherTimer _uiTimer;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.Instance.AppSettings;
        RefreshDisplay();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();

        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    private void RefreshDisplay()
    {
        PttText.Text = _settings.PushToTalkDisplay;
        RecText.Text = _settings.ToggleRecordingDisplay;
        ThresholdSlider.Value = _settings.Threshold;
        ThresholdValue.Text = $"{_settings.Threshold} dB";
    }

    private void UpdateStatus()
    {
        if (!IsVisible) return;
        var engine = App.Instance.Engine;
        if (engine.IsEngaged)
        {
            StatusText.Text = engine.IsGateOpen ? "Full Volume" : "Noise Reduced";
            StatusDetail.Text = engine.IsGateOpen ? "Your voice is passing through" : "Mic level reduced — filtering noise";
            LevelBar.Value = Math.Max(0, Math.Min(100, (engine.LatestDB + 80) / 80 * 100));
        }
        else
        {
            StatusText.Text = "Standby";
            StatusDetail.Text = "Waiting for superwhisper hotkey";
            LevelBar.Value = 0;
        }
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null) return;
        _settings.Threshold = (float)e.NewValue;
        ThresholdValue.Text = $"{(int)e.NewValue} dB";
        _settings.Save();
    }

    private void OnSync(object sender, RoutedEventArgs e)
    {
        SuperWhisperIntegration.SyncShortcuts(_settings);
        _settings.Save();
        App.Instance.Hotkeys.Unregister();
        App.Instance.Hotkeys.Register();
        RefreshDisplay();
    }
}
