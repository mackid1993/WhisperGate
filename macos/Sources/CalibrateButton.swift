import SwiftUI
import AudioToolbox
import Accelerate

struct CalibrateButton: View {
    let state: AppState
    @State private var step: CalStep = .idle
    @State private var noiseLevel: Float = -60
    @State private var speechLevel: Float = -20
    @State private var recorder: LiveRecorder?

    enum CalStep: Equatable {
        case idle
        case readyNoise
        case recordingNoise
        case readySpeech
        case recordingSpeech
        case done
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            switch step {
            case .idle:
                Button(action: { step = .readyNoise }) {
                    Label("Calibrate", systemImage: "tuningfork").font(.caption)
                }
                .buttonStyle(.bordered).controlSize(.small)

            case .readyNoise:
                VStack(alignment: .leading, spacing: 4) {
                    Text("Step 1: Room Noise").font(.caption.bold())
                    Text("Let your TV / background noise play. Don't speak.")
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Button("Start Listening") { startNoise() }
                            .buttonStyle(.borderedProminent).controlSize(.small)
                        Button("Cancel") { cancel() }
                            .buttonStyle(.bordered).controlSize(.small)
                    }
                }

            case .recordingNoise:
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Circle().fill(.red).frame(width: 8, height: 8)
                        Text("Listening to room noise...").font(.caption).foregroundStyle(.orange)
                    }
                    Button("Done") { finishNoise() }
                        .buttonStyle(.borderedProminent).controlSize(.small)
                }

            case .readySpeech:
                VStack(alignment: .leading, spacing: 4) {
                    Text("Step 2: Your Voice").font(.caption.bold())
                    Text("Room noise: \(String(format: "%.0f", noiseLevel)) dB")
                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                    Text("Keep your TV / noise playing. Now speak over it at your normal dictation volume.")
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Button("Start Listening") { startSpeech() }
                            .buttonStyle(.borderedProminent).controlSize(.small)
                        Button("Cancel") { cancel() }
                            .buttonStyle(.bordered).controlSize(.small)
                    }
                }

            case .recordingSpeech:
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Circle().fill(.red).frame(width: 8, height: 8)
                        Text("Listening to your voice...").font(.caption).foregroundStyle(.green)
                    }
                    Button("Done") { finishSpeech() }
                        .buttonStyle(.borderedProminent).controlSize(.small)
                }

            case .done:
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 4) {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
                        Text("Calibrated").font(.caption.bold())
                    }
                    Text("Noise: \(String(format: "%.0f", noiseLevel)) dB  |  Voice: \(String(format: "%.0f", speechLevel)) dB  |  Threshold: \(String(format: "%.0f", state.threshold)) dB")
                        .font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary)
                    Button(action: { step = .readyNoise }) {
                        Label("Recalibrate", systemImage: "tuningfork").font(.caption)
                    }
                    .buttonStyle(.bordered).controlSize(.small)
                }
            }
        }
        .onDisappear { cancel() }
    }

    // MARK: - Recording steps

    private func startNoise() {
        // Make sure mic is unmuted and at full volume for calibration
        if let devID = AudioDeviceManager.defaultInputDevice() {
            AudioDeviceManager.setInputMute(devID, muted: false)
            AudioDeviceManager.setInputVolume(devID, volume: 1.0)
        }
        step = .recordingNoise
        let r = LiveRecorder(deviceUID: state.inputDeviceUID)
        r.start()
        recorder = r
    }

    private func finishNoise() {
        guard let r = recorder else { return }
        let samples = r.stop()
        recorder = nil
        noiseLevel = computeLevel(samples, percentile: 0.99)
        step = .readySpeech
    }

    private func startSpeech() {
        step = .recordingSpeech
        let r = LiveRecorder(deviceUID: state.inputDeviceUID)
        r.start()
        recorder = r
    }

    private func finishSpeech() {
        guard let r = recorder else { return }
        let samples = r.stop()
        recorder = nil
        speechLevel = computeLevel(samples, percentile: 0.5)

        // Set threshold: noise + 3dB (just above the loudest noise)
        let threshold = noiseLevel + 15
        state.threshold = min(0, max(-60, round(threshold)))
        step = .done
    }

    private func cancel() {
        recorder?.stop()
        recorder = nil
        step = .idle
    }

    // MARK: - Level computation

    private func computeLevel(_ samples: [Float], percentile: Float) -> Float {
        guard !samples.isEmpty else { return -60 }
        let chunkSize = 2048
        var levels: [Float] = []
        var i = 0
        while i + chunkSize <= samples.count {
            var ms: Float = 0
            samples.withUnsafeBufferPointer { buf in
                vDSP_measqv(buf.baseAddress! + i, 1, &ms, vDSP_Length(chunkSize))
            }
            levels.append(ms > 0 ? 10 * log10(ms) : -80)
            i += chunkSize
        }
        guard !levels.isEmpty else { return -60 }
        let sorted = levels.sorted()
        let idx = min(sorted.count - 1, Int(Float(sorted.count) * percentile))
        return sorted[idx]
    }
}

// MARK: - Live recorder

class LiveRecorder {
    private var queue: AudioQueueRef?
    private var samples: [Float] = []
    private let lock = NSLock()
    private let deviceUID: String?

    init(deviceUID: String?) { self.deviceUID = deviceUID }

    deinit { stop() }

    func start() {
        // Use passRetained so the recorder stays alive while the callback runs
        var fmt = AudioStreamBasicDescription(
            mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
            mChannelsPerFrame: 1, mBitsPerChannel: 32, mReserved: 0)
        let ptr = Unmanaged.passRetained(self).toOpaque()
        guard AudioQueueNewInput(&fmt, { ud, aq, buf, _, _, _ in
            guard let ud else { return }
            let r = Unmanaged<LiveRecorder>.fromOpaque(ud).takeUnretainedValue()
            let n = Int(buf.pointee.mAudioDataByteSize) / 4
            if n > 0 {
                let p = buf.pointee.mAudioData.assumingMemoryBound(to: Float.self)
                let chunk = Array(UnsafeBufferPointer(start: p, count: n))
                r.lock.lock()
                r.samples.append(contentsOf: chunk)
                r.lock.unlock()
            }
            AudioQueueEnqueueBuffer(aq, buf, 0, nil)
        }, ptr, nil, nil, 0, &queue) == noErr, let q = queue else { return }

        if let uid = deviceUID, let devID = AudioDeviceManager.deviceByUID(uid) {
            var dev = devID
            AudioQueueSetProperty(q, kAudioQueueProperty_CurrentDevice, &dev, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        for _ in 0..<3 {
            var b: AudioQueueBufferRef?
            AudioQueueAllocateBuffer(q, 2048 * 4, &b)
            if let b { AudioQueueEnqueueBuffer(q, b, 0, nil) }
        }
        AudioQueueStart(q, nil)
    }

    @discardableResult
    func stop() -> [Float] {
        if let q = queue {
            AudioQueueStop(q, true)
            AudioQueueDispose(q, true)
            // Balance the passRetained in start()
            Unmanaged.passUnretained(self).release()
        }
        queue = nil
        lock.lock()
        let result = samples
        lock.unlock()
        return result
    }
}
