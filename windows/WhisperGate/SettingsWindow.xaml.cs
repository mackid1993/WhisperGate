using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperGate;

public sealed partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uiTimer;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.Instance.AppSettings;

        // Load current values
        RefreshDisplay();

        // Update status periodically
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = System.TimeSpan.FromMilliseconds(250);
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();

        Closed += (_, _) => _uiTimer.Stop();
    }

    private void RefreshDisplay()
    {
        PttText.Text = _settings.PushToTalkDisplay;
        RecText.Text = _settings.ToggleRecordingDisplay;
        ThresholdSlider.Value = _settings.Threshold;
        ThresholdValue.Text = $"{_settings.Threshold} dB";
        StartAtLoginToggle.IsOn = _settings.StartAtLogin;
    }

    private void UpdateStatus()
    {
        var engine = App.Instance.Engine;
        if (engine.IsEngaged)
        {
            StatusText.Text = engine.IsGateOpen ? "Full Volume" : "Noise Reduced";
            StatusDetail.Text = engine.IsGateOpen ? "Your voice is passing through" : "Mic level reduced — filtering noise";
            var normalized = System.Math.Max(0, System.Math.Min(100, (engine.LatestDB + 80) / 80 * 100));
            LevelBar.Value = normalized;
        }
        else
        {
            StatusText.Text = "Standby";
            StatusDetail.Text = "Waiting for superwhisper hotkey";
            LevelBar.Value = 0;
        }
    }

    private void OnThresholdChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
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

    private void OnStartAtLoginChanged(object sender, RoutedEventArgs e)
    {
        _settings.StartAtLogin = StartAtLoginToggle.IsOn;
        _settings.Save();
    }
}
