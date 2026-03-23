import Foundation

/// Memory-mapped file ring buffer for IPC with the HAL plugin.
/// Layout must match WGSharedHeader in WhisperGateDriver.c.
final class SharedRingBuffer {
    static let filePath = "/tmp/whispergate_audio.buf"
    private static let ringFrames = 96000
    private static let headerSize = 64

    private var mem: UnsafeMutableRawPointer?
    private var fd: Int32 = -1
    private var totalSize: Int { Self.headerSize + Self.ringFrames * 4 }

    private var header: UnsafeMutablePointer<UInt64>? {
        mem?.assumingMemoryBound(to: UInt64.self)
    }
    private var ringBuffer: UnsafeMutablePointer<Float>? {
        mem.map { ($0 + Self.headerSize).assumingMemoryBound(to: Float.self) }
    }
    private var isActivePtr: UnsafeMutablePointer<UInt32>? {
        mem.map { ($0 + 20).assumingMemoryBound(to: UInt32.self) }
    }

    func create() -> Bool {
        // Create file with explicit permissions using FileManager
        unlink(Self.filePath)
        let data = Data(count: totalSize)
        FileManager.default.createFile(atPath: Self.filePath, contents: data,
            attributes: [.posixPermissions: 0o666])

        let fd = Darwin.open(Self.filePath, O_RDWR)
        guard fd >= 0 else {
            NSLog("SharedRingBuffer: open failed errno=%d", errno)
            return false
        }
        self.fd = fd
        let ptr = mmap(nil, totalSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0)
        guard ptr != MAP_FAILED else {
            NSLog("SharedRingBuffer: mmap failed errno=%d", errno)
            Darwin.close(fd); self.fd = -1; return false
        }
        mem = ptr
        memset(ptr!, 0, totalSize)
        isActivePtr?.pointee = 1
        NSLog("SharedRingBuffer: ready at %@", Self.filePath)
        return true
    }

    func write(_ samples: UnsafePointer<Float>, count: Int) {
        guard let header, let ring = ringBuffer else { return }
        let wp = header[0]
        let startIdx = Int(wp % UInt64(Self.ringFrames))
        let endIdx = startIdx + count
        if endIdx <= Self.ringFrames {
            memcpy(ring.advanced(by: startIdx), samples, count * 4)
        } else {
            let firstPart = Self.ringFrames - startIdx
            memcpy(ring.advanced(by: startIdx), samples, firstPart * 4)
            memcpy(ring, samples.advanced(by: firstPart), (count - firstPart) * 4)
        }
        header[0] = wp &+ UInt64(count)
    }

    func writeSilence(count: Int) {
        guard let header, let ring = ringBuffer else { return }
        let wp = header[0]
        let startIdx = Int(wp % UInt64(Self.ringFrames))
        let endIdx = startIdx + count
        if endIdx <= Self.ringFrames {
            memset(ring.advanced(by: startIdx), 0, count * 4)
        } else {
            let firstPart = Self.ringFrames - startIdx
            memset(ring.advanced(by: startIdx), 0, firstPart * 4)
            memset(ring, 0, (count - firstPart) * 4)
        }
        header[0] = wp &+ UInt64(count)
    }

    func close() {
        isActivePtr?.pointee = 0
        if let m = mem { munmap(m, totalSize) }
        mem = nil
        if fd >= 0 { Darwin.close(fd); fd = -1 }
        unlink(Self.filePath)
    }

    deinit { close() }
}
