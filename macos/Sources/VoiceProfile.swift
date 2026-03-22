import Accelerate
import AudioToolbox

/// Stores a spectral fingerprint of the user's voice.
/// Captured once in a quiet room during calibration.
/// Used to match incoming audio against the user's voice.
struct VoiceProfile: Codable {
    /// Normalized spectral shape (magnitude per frequency band, 20 bands)
    let spectralShape: [Float]
    /// Average pitch range (fundamental frequency)
    let avgPitch: Float
    /// Energy distribution signature
    let energySignature: [Float]

    static let bandCount = 20
}

/// Builds a VoiceProfile from recorded speech samples.
enum VoiceProfileBuilder {
    private static let fftSize = 2048
    private static let sampleRate: Float = 48000
    private static let bandCount = VoiceProfile.bandCount

    /// Analyze raw audio samples and build a voice profile.
    static func build(from allSamples: [Float]) -> VoiceProfile? {
        guard allSamples.count >= fftSize else { return nil }

        guard let fftSetup = vDSP_DFT_zrop_CreateSetup(nil, vDSP_Length(fftSize), .FORWARD) else { return nil }
        defer { vDSP_DFT_DestroySetup(fftSetup) }

        // Process in chunks, accumulate spectral shapes
        let chunkCount = allSamples.count / fftSize
        guard chunkCount > 0 else { return nil }

        var accumulatedBands = [Float](repeating: 0, count: bandCount)
        var validChunks = 0

        for i in 0..<chunkCount {
            let offset = i * fftSize
            let chunk = Array(allSamples[offset..<(offset + fftSize)])

            // Skip quiet chunks (silence between words)
            var rms: Float = 0
            vDSP_measqv(chunk, 1, &rms, vDSP_Length(fftSize))
            if rms < 1e-6 { continue } // skip silence

            if let bands = computeBands(chunk, fftSetup: fftSetup) {
                for b in 0..<bandCount { accumulatedBands[b] += bands[b] }
                validChunks += 1
            }
        }

        guard validChunks > 0 else { return nil }

        // Average
        for b in 0..<bandCount { accumulatedBands[b] /= Float(validChunks) }

        // Normalize to sum=1 (shape, not magnitude)
        let total = accumulatedBands.reduce(0, +)
        guard total > 0 else { return nil }
        let spectralShape = accumulatedBands.map { $0 / total }

        // Simple energy signature: ratio between each adjacent band pair
        var energySig = [Float](repeating: 0, count: bandCount - 1)
        for b in 0..<(bandCount - 1) {
            energySig[b] = spectralShape[b + 1] > 0 ? spectralShape[b] / spectralShape[b + 1] : 0
        }

        return VoiceProfile(
            spectralShape: spectralShape,
            avgPitch: 150, // placeholder — could compute from autocorrelation
            energySignature: energySig
        )
    }

    /// Compare incoming audio against a voice profile. Returns 0-1 similarity score.
    static func match(samples: UnsafePointer<Float>, count: Int, profile: VoiceProfile, fftSetup: vDSP_DFT_Setup) -> Float {
        guard count >= fftSize else { return 0 }

        let chunk = Array(UnsafeBufferPointer(start: samples, count: min(count, fftSize)))

        // Check minimum energy
        var rms: Float = 0
        vDSP_measqv(chunk, 1, &rms, vDSP_Length(chunk.count))
        if rms < 1e-7 { return 0 } // silence

        guard let bands = computeBands(chunk, fftSetup: fftSetup) else { return 0 }

        // Normalize
        let total = bands.reduce(0, +)
        guard total > 0 else { return 0 }
        let shape = bands.map { $0 / total }

        // Cosine similarity between spectral shapes
        var dot: Float = 0
        var magA: Float = 0
        var magB: Float = 0
        vDSP_dotpr(shape, 1, profile.spectralShape, 1, &dot, vDSP_Length(bandCount))
        vDSP_dotpr(shape, 1, shape, 1, &magA, vDSP_Length(bandCount))
        vDSP_dotpr(profile.spectralShape, 1, profile.spectralShape, 1, &magB, vDSP_Length(bandCount))

        let denom = sqrt(magA * magB)
        guard denom > 0 else { return 0 }

        return dot / denom // 0 to 1, higher = more similar
    }

    // MARK: - FFT band computation

    private static func computeBands(_ chunk: [Float], fftSetup: vDSP_DFT_Setup) -> [Float]? {
        var windowed = [Float](repeating: 0, count: fftSize)
        var window = [Float](repeating: 0, count: fftSize)
        vDSP_hann_window(&window, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        vDSP_vmul(chunk, 1, window, 1, &windowed, 1, vDSP_Length(fftSize))

        var inputReal = [Float](repeating: 0, count: fftSize / 2)
        var inputImag = [Float](repeating: 0, count: fftSize / 2)
        for i in 0..<(fftSize / 2) {
            inputReal[i] = windowed[i * 2]
            inputImag[i] = windowed[i * 2 + 1]
        }

        var outputReal = [Float](repeating: 0, count: fftSize / 2)
        var outputImag = [Float](repeating: 0, count: fftSize / 2)
        vDSP_DFT_Execute(fftSetup, inputReal, inputImag, &outputReal, &outputImag)

        let binCount = fftSize / 2
        var magnitudes = [Float](repeating: 0, count: binCount)
        outputReal.withUnsafeMutableBufferPointer { rBuf in
            outputImag.withUnsafeMutableBufferPointer { iBuf in
                var sc = DSPSplitComplex(realp: rBuf.baseAddress!, imagp: iBuf.baseAddress!)
                vDSP_zvmags(&sc, 1, &magnitudes, 1, vDSP_Length(binCount))
            }
        }

        // Group into bands (mel-like spacing: more resolution at low freqs)
        let binHz = sampleRate / Float(fftSize) // ~23.4 Hz per bin
        var bands = [Float](repeating: 0, count: bandCount)

        // Band edges (Hz): logarithmically spaced from 80 to 8000 Hz
        let lowHz: Float = 80
        let highHz: Float = 8000
        let logLow = log(lowHz)
        let logHigh = log(highHz)

        for b in 0..<bandCount {
            let f0 = exp(logLow + (logHigh - logLow) * Float(b) / Float(bandCount))
            let f1 = exp(logLow + (logHigh - logLow) * Float(b + 1) / Float(bandCount))
            let bin0 = max(1, Int(f0 / binHz))
            let bin1 = min(binCount - 1, Int(f1 / binHz))
            guard bin1 > bin0 else { continue }

            var sum: Float = 0
            for j in bin0..<bin1 { sum += magnitudes[j] }
            bands[b] = sum / Float(bin1 - bin0)
        }

        return bands
    }
}
