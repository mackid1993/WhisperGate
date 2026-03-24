using System;
using System.Windows;
using System.Windows.Media;
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

        Closing += (_, e) => { e.Cancel = true; Hide(); _uiTimer.Stop(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _uiTimer.Start(); };
    }

    private void RefreshDisplay()
    {
        PttText.Text = _settings.PushToTalkDisplay;
        RecText.Text = _settings.ToggleRecordingDisplay;
        ThresholdSlider.Value = _settings.Threshold;
        ThresholdValue.Text = $"{_settings.Threshold} dB";
        ReductionSlider.Value = _settings.ReductionPercent;
        ReductionValue.Text = $"{_settings.ReductionPercent}%";
        TrueSilenceCheck.IsChecked = _settings.ExclusiveModeEnabled;
        GatedVolumePanel.Visibility = _settings.ExclusiveModeEnabled ? Visibility.Collapsed : Visibility.Visible;
        StartAtLoginCheck.IsChecked = _settings.StartAtLogin;
    }

    private void UpdateStatus()
    {
        if (!IsVisible) return;
        var engine = App.Instance.Engine;
        if (engine.IsEngaged)
        {
            if (engine.IsGateOpen)
            {
                StatusText.Text = "Full Volume";
                StatusDetail.Text = "Your voice is passing through";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F));
            }
            else
            {
                StatusText.Text = "Noise Reduced";
                StatusDetail.Text = "Mic level reduced — filtering noise";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFC, 0xB9, 0x38));
            }
            var norm = Math.Max(0, Math.Min(1, (engine.LatestDB + 80) / 80));
            LevelFill.Width = norm * (ActualWidth - 90);
        }
        else
        {
            StatusText.Text = "Standby";
            StatusDetail.Text = engine.StatusMessage ?? "Waiting for superwhisper hotkey";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
            LevelFill.Width = 0;
        }

        // Show status/error for true silence mode
        if (engine.StatusMessage != null && _settings.ExclusiveModeEnabled)
        {
            TrueSilenceStatus.Text = engine.StatusMessage;
            TrueSilenceStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F));
            TrueSilenceStatus.Visibility = Visibility.Visible;
        }
        else if (engine.LastError != null && _settings.ExclusiveModeEnabled)
        {
            TrueSilenceStatus.Text = engine.LastError;
            TrueSilenceStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
            TrueSilenceStatus.Visibility = Visibility.Visible;
        }
        else
        {
            TrueSilenceStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null) return;
        _settings.Threshold = (float)e.NewValue;
        ThresholdValue.Text = $"{(int)e.NewValue} dB";
        _settings.Save();
    }

    private void OnReductionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null) return;
        _settings.ReductionPercent = (float)e.NewValue;
        ReductionValue.Text = $"{(int)e.NewValue}%";
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

    private void OnTrueSilenceChanged(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.ExclusiveModeEnabled = TrueSilenceCheck.IsChecked == true;
        GatedVolumePanel.Visibility = _settings.ExclusiveModeEnabled ? Visibility.Collapsed : Visibility.Visible;
        _settings.Save();
    }

    private void OnStartAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.StartAtLogin = StartAtLoginCheck.IsChecked == true;
        _settings.Save();

        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (_settings.StartAtLogin)
                key?.SetValue("WhisperGate", $"\"{Environment.ProcessPath}\"");
            else
                key?.DeleteValue("WhisperGate", false);
        }
        catch { }
    }
}
