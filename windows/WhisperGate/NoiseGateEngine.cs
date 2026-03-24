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

    // Shared mode capture
    private WaveInEvent? _sharedCapture;

    // Exclusive mode capture
    private WasapiCapture? _exclusiveCapture;
    private WaveFormat? _exclusiveFormat;
    private readonly object _modeLock = new();

    private const float MinGatedVolume = 0.05f;

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
            _reductionFactor = Math.Max(_settings.ReductionPercent / 100f, MinGatedVolume);

            _gateIsOpen = false;
            _lastSpeechTime = 0;

            if (_settings.ExclusiveModeEnabled)
            {
                // Exclusive mode: capture at full volume, set system volume to 0
                // WaveInEvent is NOT used — only WasapiCapture exclusive
                SetVolume(0);
                StartExclusiveCapture();
            }
            else
            {
                StartSharedCapture();
                SetVolume(_savedVolume * _reductionFactor);
            }
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "whispergate_error.txt"),
                ex.ToString()); } catch { }
            DisengageGate();
        }
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
            if (db >= threshold)
            {
                _lastSpeechTime = now;
                if (_settings.ForceMaxVolume && !_settings.ExclusiveModeEnabled)
                    SetVolume(1.0f);
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                if (_settings.ExclusiveModeEnabled)
                {
                    // Switch back to exclusive capture, mute system volume
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (_gateIsOpen || _device == null) return;
                            StopSharedCapture();
                            SetVolume(0);
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
            // Open threshold: threshold - 6dB hysteresis - volume reduction in dB.
            // In exclusive mode, capture is at full volume so no compensation needed.
            // In shared mode, captured signal is quieter by 20*log10(reductionFactor).
            float reductionDB = _settings.ExclusiveModeEnabled ? 0f
                : (float)(20 * Math.Log10(Math.Max(_reductionFactor, 0.001)));
            if (db >= threshold + reductionDB - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                if (_settings.ExclusiveModeEnabled)
                {
                    // Switch to shared capture, restore volume
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (!_gateIsOpen || _device == null) return;
                            StopExclusiveCapture();
                            SetVolume(_settings.ForceMaxVolume ? 1.0f : _savedVolume);
                            StartSharedCapture();
                        }
                    });
                }
                else
                {
                    SetVolume(_settings.ForceMaxVolume ? 1.0f : _savedVolume);
                }
            }
        }
    }

    // ---- Shared mode ----

    private void StartSharedCapture()
    {
        if (_sharedCapture != null) return;
        _sharedCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(48000, 16, 1),
            BufferMilliseconds = 50
        };
        _sharedCapture.DataAvailable += (_, e) => OnAudioData(ComputeDB16(e.Buffer, e.BytesRecorded));
        _sharedCapture.StartRecording();
    }

    private void StopSharedCapture()
    {
        if (_sharedCapture == null) return;
        try { _sharedCapture.StopRecording(); } catch { }
        _sharedCapture.Dispose();
        _sharedCapture = null;
    }

    // ---- Exclusive mode ----

    private void StartExclusiveCapture()
    {
        if (_exclusiveCapture != null || _device == null) return;

        // NAudio's WasapiCapture doesn't negotiate exclusive mode properly.
        // We need to find the format the device actually supports.
        var audioClient = _device.AudioClient;
        var mixFormat = audioClient.MixFormat;

        // Try the mix format first, then fall back to common formats
        WaveFormat? exclusiveFormat = null;
        WaveFormat[] candidates = new[]
        {
            mixFormat,
            new WaveFormat(48000, 16, 1),
            new WaveFormat(44100, 16, 1),
            new WaveFormat(48000, 16, 2),
            new WaveFormat(44100, 16, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
        };

        foreach (var fmt in candidates)
        {
            if (audioClient.IsFormatSupported(AudioClientShareMode.Exclusive, fmt))
            {
                exclusiveFormat = fmt;
                break;
            }
        }

        if (exclusiveFormat == null)
            throw new InvalidOperationException("No supported exclusive mode format found");

        _exclusiveCapture = new WasapiCapture(_device, true);
        _exclusiveCapture.ShareMode = AudioClientShareMode.Exclusive;
        _exclusiveCapture.WaveFormat = exclusiveFormat;
        _exclusiveFormat = exclusiveFormat;
        _exclusiveCapture.DataAvailable += (_, e) =>
        {
            if (_exclusiveFormat.BitsPerSample >= 32)
                OnAudioData(ComputeDBFloat(e.Buffer, e.BytesRecorded));
            else if (_exclusiveFormat.BitsPerSample == 24)
                OnAudioData(ComputeDB24(e.Buffer, e.BytesRecorded));
            else
                OnAudioData(ComputeDB16(e.Buffer, e.BytesRecorded));
        };
        _exclusiveCapture.StartRecording();
    }

    private void StopExclusiveCapture()
    {
        if (_exclusiveCapture == null) return;
        try { _exclusiveCapture.StopRecording(); } catch { }
        _exclusiveCapture.Dispose();
        _exclusiveCapture = null;
    }

    // ---- RMS ----

    private static float ComputeDB16(byte[] buf, int bytes)
    {
        int n = bytes / 2;
        if (n == 0) return -160;
        double sum = 0;
        for (int i = 0; i < bytes; i += 2)
        {
            double s = BitConverter.ToInt16(buf, i) / 32768.0;
            sum += s * s;
        }
        return sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
    }

    private static float ComputeDB24(byte[] buf, int bytes)
    {
        int n = bytes / 3;
        if (n == 0) return -160;
        double sum = 0;
        for (int i = 0; i + 2 < bytes; i += 3)
        {
            int s = (buf[i] | (buf[i + 1] << 8) | ((sbyte)buf[i + 2] << 16));
            double d = s / 8388608.0;
            sum += d * d;
        }
        return sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
    }

    private static float ComputeDBFloat(byte[] buf, int bytes)
    {
        int n = bytes / 4;
        if (n == 0) return -160;
        double sum = 0;
        for (int i = 0; i < bytes; i += 4)
        {
            double s = BitConverter.ToSingle(buf, i);
            sum += s * s;
        }
        return sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
    }

    private void SetVolume(float volume)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch { }
    }
}
