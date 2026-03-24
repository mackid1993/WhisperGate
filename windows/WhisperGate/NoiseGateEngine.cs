using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private MMDevice? _device;
    private WaveInEvent? _capture;

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

            _capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
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
        int samples = e.BytesRecorded / 2;
        if (samples == 0) return;
        double sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            double s = BitConverter.ToInt16(e.Buffer, i) / 32768.0;
            sum += s * s;
        }
        float db = sum > 0 ? (float)(10 * Math.Log10(sum / samples)) : -160;
        LatestDB = db;

        if (_dbgCount++ % 20 == 0)
        {
            StatusMessage = $"superwhisper PID {_swPid} | db={db:F1} thr={_settings.Threshold:F0}";
            // Re-search for superwhisper if not found or process died
            if (_swVolume == null && _device != null)
            {
                _swVolume = FindSuperwhisperSession(_device, out _swPid);
                if (_swVolume != null && !_gateIsOpen)
                    SetSW(0f);
            }
        }

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

}
