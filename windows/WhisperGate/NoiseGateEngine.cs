using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

/// Noise gate engine — Sophist-style hard gate with system volume control.
/// Every chunk: RMS above threshold = full volume, below = zero volume.
/// No state machine, no hysteresis, no hold time.
/// Detection always works because WaveInEvent captures at whatever the
/// current system volume is — at 0% Windows delivers true silence to
/// superwhisper while our capture still gets SOME signal for detection.
public class NoiseGateEngine
{
    private readonly Settings _settings;
    private WaveInEvent? _waveIn;
    private MMDevice? _device;
    private float _savedVolume = 1f;
    private bool _gateIsOpen = true;

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

            // Start gated — volume to 0
            _gateIsOpen = false;
            SetVolume(0f);
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

    /// Sophist-style per-chunk hard gate.
    /// Above threshold = pass audio (full volume).
    /// Below threshold = silence (zero volume).
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

        bool shouldOpen = db >= _settings.Threshold;
        if (shouldOpen != _gateIsOpen)
        {
            _gateIsOpen = shouldOpen;
            SetVolume(shouldOpen ? 1f : 0f);
        }
    }

    private void SetVolume(float volume)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch { }
    }
}
