using System;
using System.Drawing;
using System.Windows.Forms;

namespace WhisperGate;

class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly TrayApp _app;
    private readonly Label _pttLabel;
    private readonly Label _recLabel;
    private readonly TrackBar _thresholdSlider;
    private readonly Label _thresholdValue;

    public SettingsForm(Settings settings, TrayApp app)
    {
        _settings = settings;
        _app = app;

        Text = "WhisperGate Settings";
        Size = new Size(420, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // Don't close, just hide
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };

        var y = 20;

        // Title
        var title = new Label { Text = "WhisperGate", Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(20, y), AutoSize = true };
        Controls.Add(title);
        y += 35;

        var subtitle = new Label { Text = "Noise gate for superwhisper", ForeColor = Color.Gray, Location = new Point(20, y), AutoSize = true };
        Controls.Add(subtitle);
        y += 35;

        // Shortcuts
        var shortcutsHeader = new Label { Text = "superwhisper Shortcuts", Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, y), AutoSize = true };
        Controls.Add(shortcutsHeader);
        y += 25;

        Controls.Add(new Label { Text = "Push to Talk:", Location = new Point(20, y), AutoSize = true });
        _pttLabel = new Label { Text = _settings.PushToTalkDisplay, ForeColor = Color.DodgerBlue, Location = new Point(200, y), AutoSize = true };
        Controls.Add(_pttLabel);
        y += 25;

        Controls.Add(new Label { Text = "Toggle Recording:", Location = new Point(20, y), AutoSize = true });
        _recLabel = new Label { Text = _settings.ToggleRecordingDisplay, ForeColor = Color.DodgerBlue, Location = new Point(200, y), AutoSize = true };
        Controls.Add(_recLabel);
        y += 25;

        var syncBtn = new Button { Text = "Sync from superwhisper", Location = new Point(20, y), AutoSize = true };
        syncBtn.Click += (_, _) =>
        {
            SuperWhisperIntegration.SyncShortcuts(_settings);
            _settings.Save();
            _app.RefreshHotkeys();
            RefreshDisplay();
        };
        Controls.Add(syncBtn);
        y += 40;

        // Threshold
        var thresholdHeader = new Label { Text = "Noise Gate Threshold", Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, y), AutoSize = true };
        Controls.Add(thresholdHeader);
        y += 25;

        _thresholdSlider = new TrackBar
        {
            Minimum = -60, Maximum = -20,
            Value = (int)_settings.Threshold,
            TickFrequency = 5,
            Location = new Point(20, y),
            Size = new Size(280, 45)
        };
        _thresholdValue = new Label { Text = $"{_settings.Threshold} dB", Location = new Point(310, y + 5), AutoSize = true };
        _thresholdSlider.ValueChanged += (_, _) =>
        {
            _settings.Threshold = _thresholdSlider.Value;
            _thresholdValue.Text = $"{_thresholdSlider.Value} dB";
            _settings.Save();
        };
        Controls.Add(_thresholdSlider);
        Controls.Add(_thresholdValue);
        y += 55;

        var helpText = new Label
        {
            Text = "Set just above your room noise level.\nLower = less filtering. Higher = more filtering.",
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8),
            Location = new Point(20, y), AutoSize = true
        };
        Controls.Add(helpText);
    }

    public void RefreshDisplay()
    {
        _pttLabel.Text = _settings.PushToTalkDisplay;
        _recLabel.Text = _settings.ToggleRecordingDisplay;
        _thresholdSlider.Value = Math.Clamp((int)_settings.Threshold, -60, -20);
        _thresholdValue.Text = $"{_settings.Threshold} dB";
    }
}
