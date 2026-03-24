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
    private bool _gateIsOpen = true;
    private double _lastSpeechTime;
    private readonly double _holdTimeMs = 300;

    // Per-app volume control for superwhisper's capture session
    private SimpleAudioVolume? _swVolume;
    private int _swPid;

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

            // Find superwhisper
            _swVolume = FindSuperwhisperSession(_device, out _swPid);
            StatusMessage = _swVolume != null
                ? $"Detected superwhisper (PID {_swPid})"
                : "superwhisper not detected — start a dictation first, then re-engage.";

            // Use WasapiCapture in shared mode for our detection
            _capture = new WasapiCapture(_device, false, 50);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();

            // Start gated
            _gateIsOpen = false;
            _lastSpeechTime = 0;
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float db = ComputeDB(e.Buffer, e.BytesRecorded, _capture?.WaveFormat);
        LatestDB = db;

        double now = Environment.TickCount64;
        float threshold = _settings.Threshold;

        if (_gateIsOpen)
        {
            if (db >= threshold)
                _lastSpeechTime = now;
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                SetSW(0f);
            }
        }
        else
        {
            if (db >= threshold - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                SetSW(1f);
            }
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
        else
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
    }
}
