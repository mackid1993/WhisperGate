import AudioToolbox
import CoreAudio
import Accelerate

/// Noise gate engine with two modes:
/// - Volume mode (fallback): reduces system mic volume when gate is closed
/// - Virtual mic mode: writes audio/silence to shared ring buffer for HAL plugin
final class NoiseGateEngine {
    private weak var state: AppState?
    private var audioQueue: AudioQueueRef?
    private var deviceID: AudioDeviceID = 0
    private var savedVolume: Float = 1.0
    private var uiTimer: Timer?

    private var gateIsOpen: Bool = false
    private var lastSpeechTime: CFAbsoluteTime = 0
    private var latestDB: Float = -160
    private var cachedThreshold: Float = -30
    private var cachedHoldTime: Double = 0.3
    private var reductionFactor: Float = 0.30

    // Virtual mic ring buffer (nil = volume mode fallback)
    private var ringBuffer: SharedRingBuffer?

    var hasProfile: Bool { true }
    func saveProfile(_ p: VoiceProfile) {}

    init(state: AppState) {
        self.state = state
        let rb = SharedRingBuffer()
        if rb.create() {
            ringBuffer = rb
        }
    }
    func prepare() { resolveDevice() }

    func engageGate() {
        guard let state, state.isEnabled, audioQueue == nil else { return }
        resolveDevice()
        savedVolume = AudioDeviceManager.getInputVolume(deviceID) ?? 1.0
        cachedThreshold = state.threshold
        cachedHoldTime = state.holdTimeMs / 1000.0
        reductionFactor = state.reductionPercent / 100.0
        latestDB = -160
        lastSpeechTime = 0

        do { try startQueue() } catch { return }

        // Start gated
        gateIsOpen = false
        if ringBuffer == nil {
            // Volume mode fallback
            AudioDeviceManager.setInputVolume(deviceID, volume: max(savedVolume * reductionFactor, 0.001))
        }
        startUITimer()
        state.isGateEngaged = true
        state.isGateOpen = false
    }

    func disengageGate() {
        stopUITimer()
        stopQueue()
        AudioDeviceManager.setInputMute(deviceID, muted: false)
        AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume)
        gateIsOpen = true
        guard let state else { return }
        state.isGateEngaged = false
        state.isGateOpen = true
        state.currentLevel = -160
        state.currentLevelSmoothed = -160
    }

    func stop() {
        if audioQueue != nil { disengageGate() }
        ringBuffer?.close()
        ringBuffer = nil
    }

    private func resolveDevice() {
        if let uid = state?.inputDeviceUID, let id = AudioDeviceManager.deviceByUID(uid) { deviceID = id }
        else if let id = AudioDeviceManager.defaultInputDevice() { deviceID = id }
    }

    // MARK: - Audio callback (matches Windows OnDataAvailable exactly)

    private func onBuffer(_ ref: AudioQueueBufferRef) {
        guard audioQueue != nil else { return }
        let n = Int(ref.pointee.mAudioDataByteSize) / 4
        guard n > 0 else { return }
        var ms: Float = 0
        vDSP_measqv(ref.pointee.mAudioData.assumingMemoryBound(to: Float.self), 1, &ms, vDSP_Length(n))
        let db: Float = ms > 0 ? 10 * log10(ms) : -160
        latestDB = db

        let now = CFAbsoluteTimeGetCurrent()

        if gateIsOpen {
            if db >= cachedThreshold {
                lastSpeechTime = now
            } else if (now - lastSpeechTime) > cachedHoldTime {
                gateIsOpen = false
                if ringBuffer == nil {
                    AudioDeviceManager.setInputVolume(deviceID, volume: max(savedVolume * reductionFactor, 0.001))
                }
            }
        } else {
            if db >= cachedThreshold - 6 {
                gateIsOpen = true
                lastSpeechTime = now
                if ringBuffer == nil {
                    AudioDeviceManager.setInputVolume(deviceID, volume: savedVolume)
                }
            }
        }

        // Write to virtual mic ring buffer
        if let rb = ringBuffer {
            let samples = ref.pointee.mAudioData.assumingMemoryBound(to: Float.self)
            if gateIsOpen {
                rb.write(samples, count: n)
            } else {
                rb.writeSilence(count: n)
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
            self.reductionFactor = state.reductionPercent / 100.0
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
