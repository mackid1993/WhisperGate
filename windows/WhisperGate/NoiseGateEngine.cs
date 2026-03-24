using System;
using System.Diagnostics;
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

    private WaveInEvent? _waveIn;

    // Per-app silence mode: control superwhisper's capture session volume
    private SimpleAudioVolume? _superwhisperVolume;

    private const float MinGatedVolume = 0.05f;

    public float LatestDB { get; private set; } = -160;
    public bool IsGateOpen => _gateIsOpen;
    public bool IsEngaged => _waveIn != null;
    public string? LastError { get; private set; }
    public string? StatusMessage { get; private set; }

    public NoiseGateEngine(Settings settings) => _settings = settings;

    public void EngageGate()
    {
        if (_waveIn != null) return;
        LastError = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _savedVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            _reductionFactor = Math.Max(_settings.ReductionPercent / 100f, MinGatedVolume);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            _gateIsOpen = false;
            _lastSpeechTime = 0;

            if (_settings.ExclusiveModeEnabled)
            {
                // Per-app silence: find superwhisper's capture session
                _superwhisperVolume = FindSuperwhisperSession(_device);
                if (_superwhisperVolume != null)
                {
                    _superwhisperVolume.Volume = 0f;
                    StatusMessage = "Detected superwhisper — true silence mode active.";
                }
                else
                {
                    StatusMessage = "Waiting for superwhisper...";
                    LastError = "Could not find superwhisper audio session. Make sure superwhisper is running.";
                    // Fall back to system volume
                    SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
                }
            }
            else
            {
                SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DisengageGate();
        }
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
        // Restore superwhisper's session volume
        if (_superwhisperVolume != null)
        {
            try { _superwhisperVolume.Volume = 1f; } catch { }
            _superwhisperVolume = null;
        }
        if (_device != null)
        {
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume; } catch { }
            _device = null;
        }
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
                if (_superwhisperVolume != null)
                    try { _superwhisperVolume.Volume = 0f; } catch { }
                else
                    SetVolume(Math.Max(_savedVolume * _reductionFactor, 0.001f));
            }
        }
        else
        {
            // Same as Mac: threshold - 6dB hysteresis
            if (db >= threshold - 6)
            {
                _gateIsOpen = true;
                _lastSpeechTime = now;
                if (_superwhisperVolume != null)
                    try { _superwhisperVolume.Volume = 1f; } catch { }
                else
                    SetVolume(_savedVolume);
            }
        }
    }

    // ---- Find superwhisper's capture session ----

    private static SimpleAudioVolume? FindSuperwhisperSession(MMDevice captureDevice)
    {
        try
        {
            var sessionManager = captureDevice.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    int pid = (int)session.GetProcessID;
                    if (pid == 0) continue;

                    var proc = Process.GetProcessById(pid);
                    if (proc.ProcessName.Contains("superwhisper", StringComparison.OrdinalIgnoreCase))
                    {
                        return session.SimpleAudioVolume;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ---- Periodically re-find superwhisper if it wasn't running at engage ----

    private double _lastSessionSearch = 0;

    private void TryFindSuperwhisper()
    {
        if (_superwhisperVolume != null || _device == null || !_settings.ExclusiveModeEnabled) return;
        double now = Environment.TickCount64;
        if (now - _lastSessionSearch < 2000) return; // search every 2s max
        _lastSessionSearch = now;
        _superwhisperVolume = FindSuperwhisperSession(_device);
        if (_superwhisperVolume != null)
        {
            StatusMessage = "Detected superwhisper — true silence mode active.";
            LastError = null;
            if (!_gateIsOpen) try { _superwhisperVolume.Volume = 0f; } catch { }
        }
    }

    private void SetVolume(float volume)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch { }
    }
}
