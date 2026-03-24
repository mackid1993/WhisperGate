using System;
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
    private double _lastStateChange;
    private readonly double _holdTimeMs = 600;
    private const double MinStateChangeMs = 500; // prevent oscillation

    private const float OpenVolume = 1.0f;
    private const float MinGatedVolume = 0.05f;
    private float _gatedVolume = 0.20f;

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

            _gatedVolume = Math.Max(_settings.ReductionPercent / 100f, MinGatedVolume);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            // Start gated at 5%
            _gateIsOpen = false;
            _lastSpeechTime = 0;
            SetVolume(_gatedVolume);
        }
        catch { DisengageGate(); }
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
            short s = BitConverter.ToInt16(e.Buffer, i);
            double n = s / 32768.0;
            sum += n * n;
        }
        float db = sum > 0 ? (float)(10 * Math.Log10(sum / samples)) : -160;
        LatestDB = db;

        double now = Environment.TickCount64;
        float threshold = _settings.Threshold;

        if (_gateIsOpen)
        {
            // At full volume — threshold comparison is straightforward
            if (db >= threshold)
            {
                _lastSpeechTime = now;
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs && (now - _lastStateChange) > MinStateChangeMs)
            {
                _gateIsOpen = false;
                _lastStateChange = now;
                SetVolume(_gatedVolume);
            }
        }
        else
        {
            // Standard compensated threshold (from audio engineering):
            // open_threshold = close_threshold + 20*log10(reductionFactor) - hysteresis
            // At 20%: 20*log10(0.20) = -14dB, minus 3dB margin = -17dB shift
            float attenuationDB = (float)(20 * Math.Log10(Math.Max(_gatedVolume, 0.01)));
            float openThreshold = threshold + attenuationDB - 3;
            if (db >= openThreshold)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now + _holdTimeMs;
                _lastStateChange = now;
                SetVolume(OpenVolume);
            }
        }
    }

    private void SetVolume(float volume)
    {
        // Open instantly (speech should come through immediately).
        // Close with a short fade to avoid harsh cutoff.
        if (_device == null) return;
        if (volume >= OpenVolume)
        {
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = volume; } catch { }
        }
        else
        {
            var dev = _device;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    float cur = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                    int steps = 5;
                    for (int i = 1; i <= steps; i++)
                    {
                        float v = cur + (volume - cur) * i / steps;
                        dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f);
                        if (i < steps) System.Threading.Thread.Sleep(3);
                    }
                }
                catch { }
            });
        }
    }
}
