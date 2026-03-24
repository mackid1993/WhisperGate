using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperGate;

public class NoiseGateEngine
{
    private readonly Settings _settings;
    private MMDevice? _device;
    private WaveInEvent? _waveIn;
    private bool _gateIsOpen = true;
    private double _lastSpeechTime;
    private readonly double _holdTimeMs = 300;

    // Per-app volume control for superwhisper
    private SimpleAudioVolume? _superwhisperVolume;
    private int _superwhisperPid;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _waveIn != null;
    public string? StatusMessage { get; private set; }

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (_waveIn != null) return;
        StatusMessage = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            // Find superwhisper's capture session
            _superwhisperVolume = FindSuperwhisperSession(_device, out _superwhisperPid);
            if (_superwhisperVolume == null)
                StatusMessage = "superwhisper not detected — make sure it is running and using the mic.";
            else
                StatusMessage = $"Detected superwhisper (PID {_superwhisperPid}) — controlling its mic volume.";

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            // Start gated — superwhisper hears silence until speech detected
            _gateIsOpen = false;
            _lastSpeechTime = 0;
            if (_superwhisperVolume != null)
                SetSuperwhisperVolume(0f);
            else
                StatusMessage = "superwhisper session not found — start a dictation in superwhisper first, then re-engage.";
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
        SetSuperwhisperVolume(1f);
        _superwhisperVolume = null;
        _device = null;
        _gateIsOpen = true;
    }

    // ---- Gate logic (identical to Mac) ----

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
            if (db >= threshold)
            {
                _lastSpeechTime = now;
            }
            else if ((now - _lastSpeechTime) > _holdTimeMs)
            {
                _gateIsOpen = false;
                SetSuperwhisperVolume(0f);
            }
        }
        else
        {
            // Same as Mac: threshold - 6dB hysteresis
            if (db >= threshold - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                SetSuperwhisperVolume(1f);
            }
        }
    }

    // ---- superwhisper session control ----

    private void SetSuperwhisperVolume(float vol)
    {
        if (_superwhisperVolume == null) return;
        try { _superwhisperVolume.Volume = vol; } catch { }
    }

    private static SimpleAudioVolume? FindSuperwhisperSession(MMDevice captureDevice, out int pid)
    {
        pid = 0;
        try
        {
            var sessions = captureDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    int p = (int)session.GetProcessID;
                    if (p == 0) continue;
                    var proc = Process.GetProcessById(p);
                    if (proc.ProcessName.Contains("superwhisper", StringComparison.OrdinalIgnoreCase))
                    {
                        pid = p;
                        return session.SimpleAudioVolume;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
}
