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

    // ---- Gate lifecycle (matches Mac exactly) ----

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
                SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
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

    // ---- Gate logic (identical to Mac NoiseGateEngine.swift) ----

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
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                if (_settings.ExclusiveModeEnabled)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
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
                    SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
                }
            }
        }
        else
        {
            // Same as Mac: threshold - 6dB hysteresis
            if (db >= threshold - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                if (_settings.ExclusiveModeEnabled)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_modeLock)
                        {
                            if (!_gateIsOpen || _device == null) return;
                            StopExclusiveCapture();
                            SetVolume(_savedVolume);
                            StartSharedCapture();
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

    // ---- Shared mode ----

    private void StartSharedCapture()
    {
        if (_sharedCapture != null) return;
        _sharedCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(48000, 16, 1),
            BufferMilliseconds = 50
        };
        _sharedCapture.DataAvailable += (_, e) => OnAudioData(ComputeDB(e.Buffer, e.BytesRecorded));
        _sharedCapture.StartRecording();
    }

    private void StopSharedCapture()
    {
        if (_sharedCapture == null) return;
        try { _sharedCapture.StopRecording(); } catch { }
        _sharedCapture.Dispose();
        _sharedCapture = null;
    }

    // ---- Exclusive mode (raw AudioClient — NAudio's WasapiCapture broken for exclusive) ----

    private void StartExclusiveCapture()
    {
        if (_exclusiveRunning || _device == null) return;
        try
        {
            // Read device's native format from property store — guaranteed for exclusive mode
            _exclusiveFormat = GetDeviceNativeFormat(_device) ?? _device.AudioClient.MixFormat;

            // Try Initialize directly — multiple strategies
            Exception? lastEx = null;
            bool ok = false;
            long[] durations = { 200 * 10000, 100 * 10000, 500 * 10000 };

            foreach (long dur in durations)
            {
                // Event-driven (periodicity = duration)
                try
                {
                    _exclusiveClient = _device.AudioClient;
                    _exclusiveClient.Initialize(AudioClientShareMode.Exclusive,
                        AudioClientStreamFlags.EventCallback, dur, dur, _exclusiveFormat, Guid.Empty);
                    ok = true; break;
                }
                catch (Exception ex) { lastEx = ex; try { _exclusiveClient?.Dispose(); } catch { } _exclusiveClient = null; }

                // Timer-driven (periodicity = 0)
                try
                {
                    _exclusiveClient = _device.AudioClient;
                    _exclusiveClient.Initialize(AudioClientShareMode.Exclusive,
                        AudioClientStreamFlags.None, dur, 0, _exclusiveFormat, Guid.Empty);
                    ok = true; break;
                }
                catch (Exception ex) { lastEx = ex; try { _exclusiveClient?.Dispose(); } catch { } _exclusiveClient = null; }
            }

            if (!ok || _exclusiveClient == null)
                throw lastEx ?? new InvalidOperationException("Exclusive mode init failed");

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
                        int pkt = _exclusiveCaptureClient.GetNextPacketSize();
                        while (pkt > 0 && _exclusiveRunning)
                        {
                            var ptr = _exclusiveCaptureClient.GetBuffer(
                                out int frames, out AudioClientBufferFlags fl);
                            int bytes = frames * bytesPerFrame;

                            if (bytes > 0 && (fl & AudioClientBufferFlags.Silent) == 0)
                            {
                                var buf = new byte[bytes];
                                Marshal.Copy(ptr, buf, 0, bytes);
                                OnAudioData(_exclusiveFormat!.BitsPerSample >= 32
                                    ? ComputeDBFloat(buf, bytes) : ComputeDB(buf, bytes));
                            }

                            _exclusiveCaptureClient.ReleaseBuffer(frames);
                            pkt = _exclusiveCaptureClient.GetNextPacketSize();
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
            SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
        }
    }

    /// Read PKEY_AudioEngine_DeviceFormat from property store — the device's native format.
    private static WaveFormat? GetDeviceNativeFormat(MMDevice device)
    {
        try
        {
            // PKEY_AudioEngine_DeviceFormat {f19f064d-082c-4e27-bc73-6882a1bb8e4c}, pid 0
            var pk = new PropertyKey(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
            var prop = device.Properties[pk];
            if (prop.Value is byte[] blob && blob.Length >= 18)
            {
                var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
                try
                {
                    var ptr = handle.AddrOfPinnedObject();
                    short tag = Marshal.ReadInt16(ptr, 0);
                    short ch = Marshal.ReadInt16(ptr, 2);
                    int rate = Marshal.ReadInt32(ptr, 4);
                    short bps = Marshal.ReadInt16(ptr, 14);

                    if (tag == unchecked((short)0xFFFE) && blob.Length >= 40)
                        return new WaveFormatExtensible(rate, bps, ch);
                    if (tag == 3)
                        return WaveFormat.CreateIeeeFloatWaveFormat(rate, ch);
                    return new WaveFormat(rate, bps, ch);
                }
                finally { handle.Free(); }
            }
        }
        catch { }
        return null;
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

    private static float ComputeDB(byte[] buf, int bytes)
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
