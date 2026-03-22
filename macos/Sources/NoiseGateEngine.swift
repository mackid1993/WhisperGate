import AudioToolbox
import CoreAudio
import Accelerate

/// Simple noise gate: calibrate room noise, then reduce mic volume by that amount
/// when you're not speaking. No muting, no peeking — just dB reduction.
/// Your voice is always detectable. CNN gets squashed.
final class NoiseGateEngine {
    private weak var state: AppState?
    private var audioQueue: AudioQueueRef?
    private var deviceID: AudioDeviceID = 0
    private var savedVolume: Float = 1.0
    private var uiTimer: Timer?

    private var gateIsOpen: Bool = true
    private var lastSpeechTime: CFAbsoluteTime = 0
    private var latestDB: Float = -160
    private var cachedThreshold: Float = -20
    private var cachedHoldTime: Double = 0.075
    private var consecutiveAbove: Int = 0

    // Fixed gentle reduction: 30% volume when gating (~-10dB)
    // Gentle enough to avoid harsh volume jumps, strong enough to suppress noise
    private let reductionFactor: Float = 0.30
    private var openThreshold: Float = -50

    var hasProfile: Bool { true }
    func saveProfile(_ p: VoiceProfile) {}

    init(state: AppState) { self.state = state }
    func prepare() { resolveDevice() }

    func engageGate() {
        guard let state, state.isEnabled, audioQueue == nil else { return }
        resolveDevice()
        savedVolume = AudioDeviceManager.getInputVolume(deviceID) ?? 1.0
        cachedThreshold = state.threshold
        cachedHoldTime = state.holdTimeMs / 1000.0
        latestDB = -160
        lastSpeechTime = CFAbsoluteTimeGetCurrent()

        // Open threshold: what your voice reads at 30% volume
        // ~10dB reduction, so shift threshold down by 10
        openThreshold = cachedThreshold - 10

        do { try startQueue() } catch { return }

        // Start at reduced volume
        gateIsOpen = false
        consecutiveAbove = 0
        AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume * reductionFactor)
        startUITimer()
        state.isGateEngaged = true
        state.isGateOpen = false
    }

    func disengageGate() {
        stopUITimer()
        stopQueue()
        // Fully restore mic state
        AudioDeviceManager.setInputMute(deviceID, muted: false)
        AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume)
        gateIsOpen = true
        consecutiveAbove = 0
        guard let state else { return }
        state.isGateEngaged = false
        state.isGateOpen = true
        state.currentLevel = -160
        state.currentLevelSmoothed = -160
    }

    /// Check if the engine is currently holding the mic
    var isActive: Bool { audioQueue != nil }

    func stop() { if audioQueue != nil { disengageGate() } }

    private func resolveDevice() {
        if let uid = state?.inputDeviceUID, let id = AudioDeviceManager.deviceByUID(uid) { deviceID = id }
        else if let id = AudioDeviceManager.defaultInputDevice() { deviceID = id }
    }

    // MARK: - Audio callback — simple, no mute/unmute cycling

    private func onBuffer(_ ref: AudioQueueBufferRef) {
        guard audioQueue != nil else { return }
        let n = Int(ref.pointee.mAudioDataByteSize) / 4
        guard n > 0 else { return }
        var ms: Float = 0
        vDSP_measqv(ref.pointee.mAudioData.assumingMemoryBound(to: Float.self), 1, &ms, vDSP_Length(n))
        let rawDB: Float = ms > 0 ? 10 * log10(ms) : -160
        latestDB = rawDB

        let now = CFAbsoluteTimeGetCurrent()

        if gateIsOpen {
            // Full volume — check if speech stopped
            if rawDB >= cachedThreshold {
                lastSpeechTime = now
            } else if (now - lastSpeechTime) > cachedHoldTime {
                // Silence — reduce volume
                gateIsOpen = false
                AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume * reductionFactor)
            }
        } else {
            // Reduced volume — check if speech started
            // Audio is quieter by reductionFactor, so use adjusted threshold
            if rawDB >= openThreshold {
                // Voice detected through reduced volume — restore full volume
                gateIsOpen = true
                lastSpeechTime = now
                AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume)
            }
        }
    }

    // MARK: - UI timer

    private func startUITimer() {
        stopUITimer()
        uiTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            guard let self, let state = self.state else { return }
            if state.isGateOpen != self.gateIsOpen { state.isGateOpen = self.gateIsOpen }
            if abs(state.currentLevel - self.latestDB) > 0.5 { state.currentLevel = self.latestDB }
            state.currentLevelSmoothed = 0.3 * self.latestDB + 0.7 * state.currentLevelSmoothed
            self.cachedThreshold = state.threshold
            self.cachedHoldTime = state.holdTimeMs / 1000.0
        }
    }

    private func stopUITimer() { uiTimer?.invalidate(); uiTimer = nil }

    // MARK: - AudioQueue

    private func startQueue() throws {
        var fmt = AudioStreamBasicDescription(
            mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
            mChannelsPerFrame: 1, mBitsPerChannel: 32, mReserved: 0)
        let ptr = Unmanaged.passUnretained(self).toOpaque()
        var q: AudioQueueRef?
        guard AudioQueueNewInput(&fmt, { ud, aq, buf, _, _, _ in
            guard let ud else { return }
            Unmanaged<NoiseGateEngine>.fromOpaque(ud).takeUnretainedValue().onBuffer(buf)
            AudioQueueEnqueueBuffer(aq, buf, 0, nil)
        }, ptr, nil, nil, 0, &q) == noErr, let q else {
            throw NSError(domain: "WG", code: 1, userInfo: nil)
        }
        var dev = deviceID
        AudioQueueSetProperty(q, kAudioQueueProperty_CurrentDevice, &dev, UInt32(MemoryLayout<AudioDeviceID>.size))
        for _ in 0..<3 {
            var b: AudioQueueBufferRef?
            AudioQueueAllocateBuffer(q, 2048 * 4, &b)
            if let b { AudioQueueEnqueueBuffer(q, b, 0, nil) }
        }
        guard AudioQueueStart(q, nil) == noErr else {
            AudioQueueDispose(q, true); throw NSError(domain: "WG", code: 2, userInfo: nil)
        }
        audioQueue = q
    }

    private func stopQueue() {
        guard let q = audioQueue else { return }
        audioQueue = nil
        AudioQueueStop(q, true)
        AudioQueueDispose(q, true)
    }
}
