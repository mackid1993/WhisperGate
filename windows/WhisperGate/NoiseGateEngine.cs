using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private MMDevice? _device;

    // Our own capture — uses a unique session GUID so it's independent
    // from the session volume changes we make to superwhisper
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private volatile bool _capturing;
    private WaveFormat? _captureFormat;
    private static readonly Guid OurSessionGuid = new("B7E3F4A1-2C8D-4E5F-9A0B-1D2E3F4A5B6C");

    // Per-app volume for superwhisper
    private SimpleAudioVolume? _swVolume;
    private int _swPid;

    private bool _gateIsOpen = true;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _capturing;
    public string? StatusMessage { get; private set; }

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (_capturing) return;
        StatusMessage = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            // Find superwhisper's session
            _swVolume = FindSuperwhisperSession(_device, out _swPid);
            StatusMessage = _swVolume != null
                ? $"Detected superwhisper (PID {_swPid})"
                : "superwhisper not detected — start dictation first.";

            // Open our OWN capture with a unique session GUID
            // This should be independent from superwhisper's session volume
            _audioClient = _device.AudioClient;
            _captureFormat = _audioClient.MixFormat;

            _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                500 * 10000, // 50ms buffer in 100ns units
                0,
                _captureFormat,
                OurSessionGuid); // KEY: separate session

            _captureClient = _audioClient.AudioCaptureClient;

            _capturing = true;
            _gateIsOpen = false;
            _audioClient.Start();

            // Set superwhisper to silence
            SetSW(0f);

            // Capture thread
            int bytesPerFrame = _captureFormat.Channels * _captureFormat.BitsPerSample / 8;
            _captureThread = new Thread(() =>
            {
                while (_capturing)
                {
                    Thread.Sleep(10);
                    if (!_capturing) break;
                    try
                    {
                        int pkt = _captureClient!.GetNextPacketSize();
                        while (pkt > 0 && _capturing)
                        {
                            var ptr = _captureClient.GetBuffer(
                                out int frames, out AudioClientBufferFlags flags);
                            int bytes = frames * bytesPerFrame;

                            if (bytes > 0 && (flags & AudioClientBufferFlags.Silent) == 0)
                            {
                                var buf = new byte[bytes];
                                Marshal.Copy(ptr, buf, 0, bytes);
                                ProcessAudio(buf, bytes);
                            }

                            _captureClient.ReleaseBuffer(frames);
                            pkt = _captureClient.GetNextPacketSize();
                        }
                    }
                    catch { }

                    // Periodically re-search for superwhisper
                    RetryFindSuperwhisper();
                }
            })
            { IsBackground = true, Priority = ThreadPriority.Highest };
            _captureThread.Start();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            DisengageGate();
        }
    }

    public void DisengageGate()
    {
        _capturing = false;
        _captureThread?.Join(1000);
        _captureThread = null;
        SetSW(1f);
        _swVolume = null;
        try { _audioClient?.Stop(); } catch { }
        try { _audioClient?.Dispose(); } catch { }
        _audioClient = null;
        _captureClient = null;
        _device = null;
        _gateIsOpen = true;
    }

    // Per-chunk hard gate
    private void ProcessAudio(byte[] buf, int bytes)
    {
        float db;
        if (_captureFormat!.BitsPerSample == 32)
        {
            int n = bytes / (4 * _captureFormat.Channels);
            if (n == 0) return;
            double sum = 0;
            int step = 4 * _captureFormat.Channels;
            for (int i = 0; i < bytes - 3; i += step)
            {
                double s = BitConverter.ToSingle(buf, i);
                sum += s * s;
            }
            db = sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
        }
        else
        {
            int n = bytes / 2;
            if (n == 0) return;
            double sum = 0;
            for (int i = 0; i < bytes; i += 2)
            {
                double s = BitConverter.ToInt16(buf, i) / 32768.0;
                sum += s * s;
            }
            db = sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
        }

        LatestDB = db;

        bool shouldOpen = db >= _settings.Threshold;
        if (shouldOpen != _gateIsOpen)
        {
            _gateIsOpen = shouldOpen;
            SetSW(shouldOpen ? 1f : 0f);
        }
    }

    // Control superwhisper's session volume
    private void SetSW(float vol)
    {
        if (_swVolume == null) return;
        try { _swVolume.Volume = vol; } catch { }
    }

    private static SimpleAudioVolume? FindSuperwhisperSession(MMDevice dev, out int pid)
    {
        pid = 0;
        try
        {
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    int p = (int)s.GetProcessID;
                    if (p == 0) continue;
                    var proc = Process.GetProcessById(p);
                    if (proc.ProcessName.Contains("superwhisper", StringComparison.OrdinalIgnoreCase))
                    {
                        pid = p;
                        return s.SimpleAudioVolume;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private double _lastRetry = 0;
    private void RetryFindSuperwhisper()
    {
        if (_swVolume != null || _device == null) return;
        double now = Environment.TickCount64;
        if (now - _lastRetry < 3000) return;
        _lastRetry = now;
        _swVolume = FindSuperwhisperSession(_device, out _swPid);
        if (_swVolume != null)
        {
            StatusMessage = $"Detected superwhisper (PID {_swPid})";
            if (!_gateIsOpen) SetSW(0f);
        }
    }
}
