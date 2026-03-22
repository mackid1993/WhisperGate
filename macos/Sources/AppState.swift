import Foundation
import SwiftUI
import CoreAudio

// MARK: - Key Combo

struct KeyCombo: Codable, Equatable {
    var keyCode: UInt16
    var modifiers: UInt64

    var displayString: String {
        var parts: [String] = []
        let flags = CGEventFlags(rawValue: modifiers)
        if flags.contains(.maskControl) { parts.append("\u{2303}") }
        if flags.contains(.maskAlternate) { parts.append("\u{2325}") }
        if flags.contains(.maskShift) { parts.append("\u{21E7}") }
        if flags.contains(.maskCommand) { parts.append("\u{2318}") }
        if keyCode > 0 && !Self.isModifierKeyCode(keyCode) {
            parts.append(Self.keyName(for: keyCode))
        }
        if parts.isEmpty { parts.append(Self.keyName(for: keyCode)) }
        return parts.joined(separator: " ")
    }

    static func isModifierKeyCode(_ kc: UInt16) -> Bool {
        (54...63).contains(kc)
    }

    static func keyName(for kc: UInt16) -> String {
        let m: [UInt16: String] = [
            0:"A",1:"S",2:"D",3:"F",4:"H",5:"G",6:"Z",7:"X",8:"C",9:"V",11:"B",
            12:"Q",13:"W",14:"E",15:"R",16:"Y",17:"T",18:"1",19:"2",20:"3",21:"4",
            22:"6",23:"5",24:"=",25:"9",26:"7",27:"-",28:"8",29:"0",30:"]",31:"O",
            32:"U",33:"[",34:"I",35:"P",36:"Return",37:"L",38:"J",39:"'",40:"K",
            41:";",42:"\\",43:",",44:"/",45:"N",46:"M",47:".",48:"Tab",49:"Space",
            50:"`",51:"Delete",53:"Esc",
            54:"R\u{2318}",55:"\u{2318}",56:"\u{21E7}",57:"CapsLock",
            58:"\u{2325}",59:"\u{2303}",60:"R\u{21E7}",61:"R\u{2325}",62:"R\u{2303}",63:"Fn",
            96:"F5",97:"F6",98:"F7",99:"F3",100:"F8",101:"F9",103:"F11",109:"F10",
            111:"F12",122:"F1",120:"F2",118:"F4",
            123:"\u{2190}",124:"\u{2192}",125:"\u{2193}",126:"\u{2191}",
        ]
        return m[kc] ?? "Key\(kc)"
    }
}

// MARK: - App State

@Observable
final class AppState {
    static let shared = AppState()

    // Runtime setup state (checked every launch based on actual permissions)
    var needsSetup: Bool = false

    private var didFinishInit = false

    // Persisted
    var isEnabled: Bool = true { didSet { guard didFinishInit else { return }; save("isEnabled", isEnabled) } }
    var threshold: Float = -12 { didSet { guard didFinishInit else { return }; save("threshold", threshold) } }
    var reductionPercent: Float = 30 { didSet { guard didFinishInit else { return }; save("reductionPercent", reductionPercent) } }
    var holdTimeMs: Double = 400 { didSet { guard didFinishInit else { return }; save("holdTimeMs", holdTimeMs) } }
    var inputDeviceUID: String? {
        didSet {
            guard didFinishInit else { return }
            if let v = inputDeviceUID { UserDefaults.standard.set(v, forKey: "inputDeviceUID") }
            else { UserDefaults.standard.removeObject(forKey: "inputDeviceUID") }
        }
    }
    var pushToTalkShortcut: KeyCombo? { didSet { guard didFinishInit else { return }; saveCombo(pushToTalkShortcut, "ptt") } }
    var recordingShortcut: KeyCombo? { didSet { guard didFinishInit else { return }; saveCombo(recordingShortcut, "rec") } }
    var startAtLogin: Bool = false { didSet { guard didFinishInit else { return }; save("startAtLogin", startAtLogin); LoginItemManager.setEnabled(startAtLogin) } }
    // Runtime
    var isGateEngaged: Bool = false
    var isGateOpen: Bool = true
    var currentLevel: Float = -160
    var currentLevelSmoothed: Float = -160
    var micSupportsVolume: Bool = true
    var isPTTHeld: Bool = false
    var isRecordingToggled: Bool = false
    var errorMessage: String?

    // Components
    var engine: NoiseGateEngine!
    var hotkeys: HotkeyMonitor!

    var menuBarIcon: String {
        if !isEnabled { return "mic.slash" }
        if isGateEngaged && !isGateOpen { return "waveform.badge.minus" }
        if isGateEngaged { return "waveform.badge.mic" }
        return "mic"
    }

    var statusText: String {
        if !isEnabled { return "Disabled" }
        if isGateEngaged && !isGateOpen { return "Noise Gated" }
        if isGateEngaged { return "Listening" }
        return "Standby"
    }

    var statusColor: Color {
        if !isEnabled { return .secondary }
        if isGateEngaged && !isGateOpen { return .orange }
        if isGateEngaged { return .green }
        return .secondary
    }

    init() {
        let d = UserDefaults.standard
        isEnabled = d.object(forKey: "isEnabled") as? Bool ?? true
        threshold = d.object(forKey: "threshold") as? Float ?? -40
        reductionPercent = d.object(forKey: "reductionPercent") as? Float ?? 30
        holdTimeMs = d.object(forKey: "holdTimeMs") as? Double ?? 300
        inputDeviceUID = d.string(forKey: "inputDeviceUID")
        pushToTalkShortcut = loadCombo("ptt")
        recordingShortcut = loadCombo("rec")
        startAtLogin = d.bool(forKey: "startAtLogin")
        didFinishInit = true
    }

    func setup() {
        // Restore mic volume in case a previous session left it muted
        if let devID = AudioDeviceManager.defaultInputDevice() {
            AudioDeviceManager.setInputVolume(devID, volume: 1.0)
        }
        if pushToTalkShortcut == nil && recordingShortcut == nil {
            syncFromSuperWhisper()
        }
        engine = NoiseGateEngine(state: self)
        hotkeys = HotkeyMonitor(state: self)
        hotkeys.start()
    }

    func teardown() {
        engine?.disengageGate()
        hotkeys?.stop()
    }

    func syncFromSuperWhisper() {
        guard SuperWhisperIntegration.isInstalled() else { return }
        if let p = SuperWhisperIntegration.readPushToTalk() {
            pushToTalkShortcut = p
        }
        if let r = SuperWhisperIntegration.readToggleRecording() {
            recordingShortcut = r
        }
        // Re-register hotkeys with new shortcuts
        hotkeys?.stop()
        hotkeys?.start()
    }

    private func save(_ k: String, _ v: Any) { UserDefaults.standard.set(v, forKey: k) }
    private func saveCombo(_ c: KeyCombo?, _ k: String) {
        if let c, let d = try? JSONEncoder().encode(c) { UserDefaults.standard.set(d, forKey: k) }
        else { UserDefaults.standard.removeObject(forKey: k) }
    }
    private func loadCombo(_ k: String) -> KeyCombo? {
        guard let d = UserDefaults.standard.data(forKey: k) else { return nil }
        return try? JSONDecoder().decode(KeyCombo.self, from: d)
    }
}
