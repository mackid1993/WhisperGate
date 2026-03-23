using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private WaveInEvent? _waveIn;
    private MMDevice? _device;
    private float _savedVolume = 1f;
    private bool _gateIsOpen = true;
    private double _lastSpeechTime;
    private float _reductionFactor = 0.30f;
    private readonly double _holdTimeMs = 300;

    // Pulsed detection for 0% gated volume
    private Timer? _pulseTimer;
    private bool _isPulsing;
    private bool _isSilentMode; // true when gated volume is effectively 0

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _waveIn != null;

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (_waveIn != null) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _savedVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            _gateIsOpen = false;
            _lastSpeechTime = 0;
            _reductionFactor = Math.Max(_settings.ReductionPercent / 100f, 0.001f);
            _isSilentMode = _settings.ReductionPercent < 1f; // 0% = silent mode

            if (_isSilentMode)
            {
                SetVolume(0);
                StartPulseTimer();
            }
            else
            {
                SetVolume(_savedVolume * _reductionFactor);
            }
        }
        catch { DisengageGate(); }
    }

    public void DisengageGate()
    {
        StopPulseTimer();
        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume; } catch { }
            _device = null;
        }
        _gateIsOpen = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int samples = e.BytesRecorded / 2;
        if (samples == 0) return;

        double sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            double n = sample / 32768.0;
            sum += n * n;
        }
        float db = sum > 0 ? (float)(10 * Math.Log10(sum / samples)) : -160;
        LatestDB = db;

        double now = Environment.TickCount64;
        float threshold = _settings.Threshold;

        if (_gateIsOpen)
        {
            if (db >= threshold) _lastSpeechTime = now;
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                if (_isSilentMode)
                {
                    SetVolume(0);
                    StartPulseTimer();
                }
                else
                {
                    SetVolume(_savedVolume * _reductionFactor);
                }
            }
        }
        else if (_isSilentMode)
        {
            // Silent mode: only check during pulse windows
            if (_isPulsing && db > -120)
            {
                // Got real audio during pulse — check for speech
                _isPulsing = false;
                SetVolume(0);
                if (db >= threshold - 6)
                {
                    _gateIsOpen = true;
                    _lastSpeechTime = now;
                    StopPulseTimer();
                    SetVolume(_savedVolume);
                }
            }
        }
        else
        {
            // Normal mode: continuous detection
            if (db >= threshold - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                SetVolume(_savedVolume);
            }
        }
    }

    private void StartPulseTimer()
    {
        StopPulseTimer();
        _pulseTimer = new Timer(_ =>
        {
            if (_waveIn == null || _gateIsOpen) return;
            // Briefly raise volume for one capture buffer
            SetVolume(_savedVolume * 0.05f); // 5% — enough for detection
            _isPulsing = true;
        }, null, 200, 200);
    }

    private void StopPulseTimer()
    {
        _pulseTimer?.Dispose();
        _pulseTimer = null;
        _isPulsing = false;
    }

    private void SetVolume(float volume)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch { }
    }
}
