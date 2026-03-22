import Foundation
import AppKit

enum SuperWhisperIntegration {
    private static let bundleID = "com.superduper.superwhisper"

    static func isInstalled() -> Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    static func readToggleRecording() -> KeyCombo? {
        readShortcut(name: "toggleRecording")
    }

    static func readPushToTalk() -> KeyCombo? {
        readShortcut(name: "pushToTalk")
    }

    /// Reads a keyboard shortcut from SuperWhisper's UserDefaults.
    /// SuperWhisper uses the KeyboardShortcuts library which stores JSON like:
    ///   {"carbonKeyCode":49,"carbonModifiers":2048,"mouseButtonNumbers":[]}
    /// These are always in ~/Library/Preferences/com.superduper.superwhisper.plist
    /// regardless of where the user sets their appFolderDirectory.
    private static func readShortcut(name: String) -> KeyCombo? {
        let key = "KeyboardShortcuts_\(name)"

        // UserDefaults(suiteName:) reads from the app's standard plist
        guard let defaults = UserDefaults(suiteName: bundleID),
              let raw = defaults.string(forKey: key),
              let data = raw.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let keyCode = json["carbonKeyCode"] as? Int,
              let modifiers = json["carbonModifiers"] as? Int else {
            return nil
        }

        let cgFlags = carbonToCGEventFlags(UInt32(modifiers))
        return KeyCombo(keyCode: UInt16(keyCode), modifiers: cgFlags.rawValue)
    }

    // Carbon modifier flag constants:
    //   cmdKey    = 256  (0x100)
    //   shiftKey  = 512  (0x200)
    //   optionKey = 2048 (0x800)
    //   controlKey = 4096 (0x1000)
    private static func carbonToCGEventFlags(_ carbon: UInt32) -> CGEventFlags {
        var flags: UInt64 = 0
        if carbon & 256   != 0 { flags |= CGEventFlags.maskCommand.rawValue }
        if carbon & 512   != 0 { flags |= CGEventFlags.maskShift.rawValue }
        if carbon & 2048  != 0 { flags |= CGEventFlags.maskAlternate.rawValue }
        if carbon & 4096  != 0 { flags |= CGEventFlags.maskControl.rawValue }
        return CGEventFlags(rawValue: flags)
    }
}
