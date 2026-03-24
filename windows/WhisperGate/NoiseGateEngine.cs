using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private MMDevice? _device;
    private WasapiCapture? _capture;

    // Per-app volume control for superwhisper's capture session
    private SimpleAudioVolume? _swVolume;
    private int _swPid;

    // Gate state for UI display
    private bool _gateIsOpen = true;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _capture != null;
    public string? StatusMessage { get; private set; }

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (_capture != null) return;
        StatusMessage = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _swVolume = FindSuperwhisperSession(_device, out _swPid);
            StatusMessage = _swVolume != null
                ? $"Detected superwhisper (PID {_swPid})"
                : "superwhisper not detected — start a dictation first, then re-engage.";

            _capture = new WasapiCapture(_device, false, 50);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();

            // Start gated
            _gateIsOpen = false;
            SetSW(0f);
        }
        catch { DisengageGate(); }
    }

    public void DisengageGate()
    {
        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        SetSW(1f);
        _swVolume = null;
        _device = null;
        _gateIsOpen = true;
    }

    // ---- Per-chunk gate decision (Sophist pattern) ----
    // Every chunk: RMS above threshold = pass audio, below = silence.
    // No state machine. No hysteresis. No hold time.
    // The per-app volume control IS the chunk replacement.

    private int _dbgCount = 0;
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float db = ComputeDB(e.Buffer, e.BytesRecorded, _capture?.WaveFormat);
        LatestDB = db;

        // Show actual values for debugging
        if (_dbgCount++ % 20 == 0 && _capture != null)
            StatusMessage = $"superwhisper PID {_swPid} | db={db:F1} thr={_settings.Threshold:F0} fmt={_capture.WaveFormat}";

        bool shouldOpen = db >= _settings.Threshold;

        if (shouldOpen != _gateIsOpen)
        {
            _gateIsOpen = shouldOpen;
            SetSW(shouldOpen ? 1f : 0f);
        }
    }

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

    private static float ComputeDB(byte[] buf, int bytes, WaveFormat? fmt)
    {
        if (fmt == null) return -160;
        if (fmt.BitsPerSample == 32)
        {
            int n = bytes / (4 * fmt.Channels);
            if (n == 0) return -160;
            double sum = 0;
            int step = 4 * fmt.Channels;
            // Use first channel only for RMS
            for (int i = 0; i < bytes - 3; i += step)
            {
                double s = BitConverter.ToSingle(buf, i);
                sum += s * s;
            }
            return sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
        }
        else
        {
            int n = bytes / (2 * fmt.Channels);
            if (n == 0) return -160;
            double sum = 0;
            int step = 2 * fmt.Channels;
            for (int i = 0; i < bytes - 1; i += step)
            {
                double s = BitConverter.ToInt16(buf, i) / 32768.0;
                sum += s * s;
            }
            return sum > 0 ? (float)(10 * Math.Log10(sum / n)) : -160;
        }
    }
}
