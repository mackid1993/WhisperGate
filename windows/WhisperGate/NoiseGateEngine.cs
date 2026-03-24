using System;
using System.Threading;
using System.Runtime.InteropServices;
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

    private WaveInEvent? _sharedCapture;
    // Exclusive mode — raw AudioClient, bypassing NAudio's broken WasapiCapture
    private AudioClient? _exclusiveClient;
    private AudioCaptureClient? _exclusiveCaptureClient;
    private Thread? _exclusiveThread;
    private volatile bool _exclusiveRunning;
    private WaveFormat? _exclusiveFormat;
    private readonly object _modeLock = new();

    private const float MinGatedVolume = 0.05f;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _sharedCapture != null || _exclusiveRunning;
    public string? LastError { get; private set; }

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (IsEngaged) return;
        LastError = null;
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
                StartExclusiveCapture();
            }
            else
            {
                StartSharedCapture();
                SetVolume(_reductionFactor);
            }
        }
        catch (Exception ex)
        {
            LastError = _settings.ExclusiveModeEnabled
                ? $"Exclusive mode failed: {ex.Message}"
                : ex.Message;
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
                // Always force full volume while speaking
                if (!_settings.ExclusiveModeEnabled)
                    SetVolume(1.0f);
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                if (_settings.ExclusiveModeEnabled)
                {
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
                    SetVolume(_reductionFactor);
                }
            }
        }
        else
        {
            // Volume drops from 1.0 (open) to _reductionFactor (closed).
            // Captured signal is lower by 20*log10(reductionFactor).
            // Subtract 4dB margin so the gate opens reliably.
            float openThreshold;
            if (_settings.ExclusiveModeEnabled)
            {
                openThreshold = threshold - 6;
            }
            else
            {
                float dropDB = (float)(20 * Math.Log10(Math.Max(_reductionFactor, 0.001)));
                openThreshold = threshold + dropDB - 8;
            }

            if (db >= openThreshold)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                if (_settings.ExclusiveModeEnabled)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (!_gateIsOpen || _device == null) return;
                            StopExclusiveCapture();
                            SetVolume(1.0f);
                            StartSharedCapture();
                        }
                    });
                }
                else
                {
                    SetVolume(1.0f);
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

    // ---- Exclusive mode (raw AudioClient, bypasses NAudio bugs) ----
    // NAudio's IsFormatSupported passes non-null ppClosestMatch for exclusive mode
    // which violates the WASAPI spec. Skip it — use MixFormat and try Initialize directly.

    private void StartExclusiveCapture()
    {
        if (_exclusiveRunning || _device == null) return;
        try
        {
            // MixFormat is shared-mode (typically float32) — exclusive mode often
            // needs plain PCM. Try multiple formats × multiple init strategies.
            var mixFmt = _device.AudioClient.MixFormat;
            WaveFormat[] formats = {
                mixFmt,
                new WaveFormat(48000, 16, mixFmt.Channels),
                new WaveFormat(44100, 16, mixFmt.Channels),
                new WaveFormat(48000, 16, 1),
                new WaveFormat(44100, 16, 1),
                new WaveFormat(48000, 16, 2),
                new WaveFormat(48000, 24, mixFmt.Channels),
                WaveFormat.CreateIeeeFloatWaveFormat(48000, mixFmt.Channels),
            };
            (AudioClientStreamFlags flags, long dur, long per)[] modes = {
                (AudioClientStreamFlags.EventCallback, 200 * 10000, 200 * 10000),
                (AudioClientStreamFlags.EventCallback, 100 * 10000, 100 * 10000),
                (AudioClientStreamFlags.None, 200 * 10000, 0),
                (AudioClientStreamFlags.None, 100 * 10000, 0),
            };

            Exception? lastEx = null;
            bool initialized = false;
            foreach (var fmt in formats)
            {
                foreach (var (sFlags, dur, per) in modes)
                {
                    try
                    {
                        _exclusiveClient = _device.AudioClient;
                        _exclusiveClient.Initialize(
                            AudioClientShareMode.Exclusive, sFlags,
                            dur, per, fmt, Guid.Empty);
                        _exclusiveFormat = fmt;
                        initialized = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { _exclusiveClient.Dispose(); } catch { }
                        _exclusiveClient = null;
                    }
                }
                if (initialized) break;
            }

            if (!initialized || _exclusiveClient == null)
                throw lastEx ?? new InvalidOperationException("All exclusive mode attempts failed");

            _exclusiveCaptureClient = _exclusiveClient.AudioCaptureClient;
            int bytesPerFrame = _exclusiveFormat.Channels * _exclusiveFormat.BitsPerSample / 8;

            _exclusiveRunning = true;
            _exclusiveClient.Start();

            _exclusiveThread = new Thread(() =>
            {
                while (_exclusiveRunning)
                {
                    Thread.Sleep(5);
                    if (!_exclusiveRunning) break;
                    try
                    {
                        int packetSize = _exclusiveCaptureClient.GetNextPacketSize();
                        while (packetSize > 0 && _exclusiveRunning)
                        {
                            var ptr = _exclusiveCaptureClient.GetBuffer(
                                out int framesAvailable,
                                out AudioClientBufferFlags flags);

                            int bytesAvailable = framesAvailable * bytesPerFrame;

                            if (bytesAvailable > 0 && (flags & AudioClientBufferFlags.Silent) == 0)
                            {
                                var buf = new byte[bytesAvailable];
                                Marshal.Copy(ptr, buf, 0, bytesAvailable);

                                if (_exclusiveFormat.BitsPerSample >= 32)
                                    OnAudioData(ComputeDBFloat(buf, bytesAvailable));
                                else
                                    OnAudioData(ComputeDB(buf, bytesAvailable));
                            }

                            _exclusiveCaptureClient.ReleaseBuffer(framesAvailable);
                            packetSize = _exclusiveCaptureClient.GetNextPacketSize();
                        }
                    }
                    catch { }
                }
            })
            { IsBackground = true, Priority = ThreadPriority.Highest };
            _exclusiveThread.Start();
        }
        catch (Exception ex)
        {
            StopExclusiveCapture();
            LastError = $"Exclusive mode failed: {ex.Message}";
            StartSharedCapture();
            SetVolume(_reductionFactor);
        }
    }

    private void StopExclusiveCapture()
    {
        _exclusiveRunning = false;
        _exclusiveThread?.Join(1000);
        _exclusiveThread = null;
        try { _exclusiveClient?.Stop(); } catch { }
        try { _exclusiveClient?.Dispose(); } catch { }
        _exclusiveClient = null;
        _exclusiveCaptureClient = null;
    }

    // ---- RMS ----

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
