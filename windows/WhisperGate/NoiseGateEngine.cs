using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

class NoiseGateEngine
{
    private readonly Settings _settings;
    private WaveInEvent? _waveIn;
    private MMDevice? _device;
    private float _savedVolume = 1f;
    private bool _gateIsOpen = true;
    private double _lastSpeechTime;
    private readonly float _reductionFactor = 0.30f; // 30% volume when gating (~10dB reduction)
    private readonly double _holdTimeMs = 300;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _waveIn != null;

    public NoiseGateEngine(Settings settings)
    {
        _settings = settings;
    }

    public void EngageGate()
    {
        if (_waveIn != null) return;

        try
        {
            // Get the default capture device for volume control
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _savedVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;

            // Start audio capture for level monitoring
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            // Start gated (reduced volume)
            _gateIsOpen = false;
            _lastSpeechTime = 0;
            SetVolume(_savedVolume * _reductionFactor);
        }
        catch
        {
            DisengageGate();
        }
    }

    public void DisengageGate()
    {
        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }

        // Restore volume
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume; } catch { }
            _device = null;
        }

        _gateIsOpen = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Compute RMS level
        int samples = e.BytesRecorded / 2; // 16-bit = 2 bytes per sample
        if (samples == 0) return;

        double sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            double normalized = sample / 32768.0;
            sum += normalized * normalized;
        }
        double rms = Math.Sqrt(sum / samples);
        float db = rms > 0 ? (float)(20 * Math.Log10(rms)) : -160;
        LatestDB = db;

        double now = Environment.TickCount64;
        float threshold = _settings.Threshold;

        if (_gateIsOpen)
        {
            // Full volume — check if speech stopped
            if (db >= threshold)
            {
                _lastSpeechTime = now;
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                // Silence — reduce volume
                _gateIsOpen = false;
                SetVolume(_savedVolume * _reductionFactor);
            }
        }
        else
        {
            // Reduced volume — check if speech started
            // Adjust threshold for reduced volume (~10dB lower)
            float openThreshold = threshold - 10;
            if (db >= openThreshold)
            {
                // Voice detected — restore full volume
                _gateIsOpen = true;
                _lastSpeechTime = now;
                SetVolume(_savedVolume);
            }
        }
    }

    private void SetVolume(float volume)
    {
        try
        {
            if (_device != null)
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
        }
        catch { }
    }
}
