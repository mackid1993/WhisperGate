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

    // 20% gated volume: enough signal for reliable detection in feedback
    // topology, small enough that STT ignores it, and the 20%→100% jump
    // is only 14dB (much smoother than 5%→100% at 26dB).
    private const float GatedVolume = 0.20f;
    private const float OpenVolume = 1.0f;

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

            // Start gated at 5%
            _gateIsOpen = false;
            _lastSpeechTime = 0;
            SetVolume(GatedVolume);
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
                SetVolume(GatedVolume);
            }
        }
        else
        {
            // Standard compensated threshold (from audio engineering):
            // open_threshold = close_threshold + 20*log10(reductionFactor) - hysteresis
            // At 20%: 20*log10(0.20) = -14dB, minus 3dB margin = -17dB shift
            float attenuationDB = (float)(20 * Math.Log10(GatedVolume));
            float openThreshold = threshold + attenuationDB - 3;
            if (db >= openThreshold && (now - _lastStateChange) > MinStateChangeMs)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now + _holdTimeMs;
                _lastStateChange = now;
                SetVolume(OpenVolume);
            }
        }
    }

    private volatile float _targetVolume = 1f;
    private System.Threading.Thread? _rampThread;

    private void SetVolume(float target)
    {
        _targetVolume = target;
        if (_rampThread == null || !_rampThread.IsAlive)
        {
            var dev = _device;
            _rampThread = new System.Threading.Thread(() =>
            {
                try
                {
                    while (dev != null)
                    {
                        float cur = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                        float tgt = _targetVolume;
                        if (Math.Abs(cur - tgt) < 0.01f)
                        {
                            dev.AudioEndpointVolume.MasterVolumeLevelScalar = tgt;
                            break;
                        }
                        // Smooth ramp: move 30% of the way each step
                        float next = cur + (tgt - cur) * 0.3f;
                        dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(next, 0f, 1f);
                        System.Threading.Thread.Sleep(5);
                    }
                }
                catch { }
            })
            { IsBackground = true };
            _rampThread.Start();
        }
    }
}
