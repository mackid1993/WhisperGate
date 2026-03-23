using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private MMDevice? _device;
    private float _savedVolume = 1f;
    private bool _gateIsOpen = true;
    private double _lastSpeechTime;
    private float _reductionFactor = 0.30f;
    private readonly double _holdTimeMs = 300;

    // Shared mode capture (used when gate is open — both us and superwhisper hear audio)
    private WaveInEvent? _sharedCapture;

    // Exclusive mode capture (used when gate is closed — we hear audio, superwhisper gets silence)
    private WasapiCapture? _exclusiveCapture;

    // True when gated volume is 0% and we use exclusive mode switching
    private bool _isSilentMode;
    private readonly object _modeLock = new();

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _sharedCapture != null || _exclusiveCapture != null;

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (IsEngaged) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _savedVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;

            _reductionFactor = Math.Max(_settings.ReductionPercent / 100f, 0.001f);
            _isSilentMode = _settings.ReductionPercent < 1f;

            _gateIsOpen = false;
            _lastSpeechTime = 0;

            if (_isSilentMode)
            {
                // Start in exclusive mode — superwhisper gets silence
                StartExclusiveCapture();
            }
            else
            {
                // Start in shared mode with reduced volume
                StartSharedCapture();
                SetVolume(_savedVolume * _reductionFactor);
            }
        }
        catch { DisengageGate(); }
    }

    public void DisengageGate()
    {
        lock (_modeLock)
        {
            StopSharedCapture();
            StopExclusiveCapture();
        }
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume; } catch { }
            _device = null;
        }
        _gateIsOpen = true;
    }

    private void OnAudioData(float db)
    {
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
                    // Switch to exclusive mode — silences superwhisper
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (_gateIsOpen || _device == null) return;
                            StopSharedCapture();
                            StartExclusiveCapture();
                        }
                    });
                }
                else
                {
                    SetVolume(_savedVolume * _reductionFactor);
                }
            }
        }
        else
        {
            float openThreshold = _isSilentMode ? threshold - 6 : threshold - 6;
            if (db >= openThreshold)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                if (_isSilentMode)
                {
                    // Switch to shared mode — superwhisper resumes
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (!_gateIsOpen || _device == null) return;
                            StopExclusiveCapture();
                            StartSharedCapture();
                            SetVolume(_savedVolume);
                        }
                    });
                }
                else
                {
                    SetVolume(_savedVolume);
                }
            }
        }
    }

    // ---- Shared mode (WaveInEvent) ----

    private void StartSharedCapture()
    {
        if (_sharedCapture != null) return;
        _sharedCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(48000, 16, 1),
            BufferMilliseconds = 50
        };
        _sharedCapture.DataAvailable += OnSharedData;
        _sharedCapture.StartRecording();
    }

    private void StopSharedCapture()
    {
        if (_sharedCapture == null) return;
        _sharedCapture.StopRecording();
        _sharedCapture.DataAvailable -= OnSharedData;
        _sharedCapture.Dispose();
        _sharedCapture = null;
    }

    private void OnSharedData(object? sender, WaveInEventArgs e)
    {
        OnAudioData(ComputeDB(e.Buffer, e.BytesRecorded));
    }

    // ---- Exclusive mode (WasapiCapture) ----

    private void StartExclusiveCapture()
    {
        if (_exclusiveCapture != null || _device == null) return;
        try
        {
            _exclusiveCapture = new WasapiCapture(_device, false, 50);
            _exclusiveCapture.ShareMode = AudioClientShareMode.Exclusive;
            _exclusiveCapture.DataAvailable += OnExclusiveData;
            _exclusiveCapture.StartRecording();
        }
        catch
        {
            // Exclusive mode failed — fall back to shared with volume floor
            _exclusiveCapture = null;
            _isSilentMode = false;
            _reductionFactor = 0.02f;
            StartSharedCapture();
            SetVolume(_savedVolume * _reductionFactor);
        }
    }

    private void StopExclusiveCapture()
    {
        if (_exclusiveCapture == null) return;
        try { _exclusiveCapture.StopRecording(); } catch { }
        _exclusiveCapture.DataAvailable -= OnExclusiveData;
        _exclusiveCapture.Dispose();
        _exclusiveCapture = null;
    }

    private void OnExclusiveData(object? sender, WaveInEventArgs e)
    {
        // Exclusive mode may use float32 format — handle both int16 and float32
        if (_exclusiveCapture?.WaveFormat.BitsPerSample == 32)
            OnAudioData(ComputeDBFloat(e.Buffer, e.BytesRecorded));
        else
            OnAudioData(ComputeDB(e.Buffer, e.BytesRecorded));
    }

    // ---- RMS calculation ----

    private static float ComputeDB(byte[] buffer, int bytes)
    {
        int samples = bytes / 2;
        if (samples == 0) return -160;
        double sum = 0;
        for (int i = 0; i < bytes; i += 2)
        {
            short s = BitConverter.ToInt16(buffer, i);
            double n = s / 32768.0;
            sum += n * n;
        }
        return sum > 0 ? (float)(10 * Math.Log10(sum / samples)) : -160;
    }

    private static float ComputeDBFloat(byte[] buffer, int bytes)
    {
        int samples = bytes / 4;
        if (samples == 0) return -160;
        double sum = 0;
        for (int i = 0; i < bytes; i += 4)
        {
            float s = BitConverter.ToSingle(buffer, i);
            sum += s * s;
        }
        return sum > 0 ? (float)(10 * Math.Log10(sum / samples)) : -160;
    }

    private void SetVolume(float volume)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch { }
    }
}
