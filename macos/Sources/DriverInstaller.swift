import Foundation

enum DriverInstaller {
    private static let driverName = "WhisperGateAudio.driver"
    private static var installDir: URL {
        URL(fileURLWithPath: "/Library/Audio/Plug-Ins/HAL")
    }
    private static var installedPath: URL {
        installDir.appendingPathComponent(driverName)
    }
    private static var bundledPath: URL? {
        Bundle.main.resourceURL?.appendingPathComponent(driverName)
    }

    static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: installedPath.path)
    }

    static func install() {
        guard let src = bundledPath else { return }
        let dst = installedPath.path
        let srcPath = src.path
        runPrivileged("rm -rf '\(dst)' && cp -R '\(srcPath)' '\(dst)' && killall coreaudiod")
    }

    static func uninstall() {
        guard isInstalled else { return }
        // Stop engine from writing to ring buffer before removing driver
        AppState.shared.engine?.stop()
        // Clean up shared memory file
        unlink(SharedRingBuffer.filePath)
        let dst = installedPath.path
        runPrivileged("rm -rf '\(dst)' && killall coreaudiod")
        // Restart engine without ring buffer (volume fallback mode)
        AppState.shared.engine = NoiseGateEngine(state: AppState.shared)
    }

    private static func runPrivileged(_ command: String) {
        let script = "do shell script \"\(command)\" with administrator privileges"
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        task.arguments = ["-e", script]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
    }
}
