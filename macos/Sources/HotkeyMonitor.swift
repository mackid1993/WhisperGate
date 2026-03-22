import Foundation
import Carbon.HIToolbox
import CoreGraphics

/// Detects superwhisper hotkeys using two mechanisms, NEITHER requiring permissions:
/// 1. Carbon RegisterEventHotKey for combo shortcuts (e.g. Option+Space)
/// 2. CGEventSource.flagsState polling for modifier-only shortcuts (e.g. Right Option)
final class HotkeyMonitor {
    private let state: AppState
    private var hotKeyRefs: [EventHotKeyRef?] = []
    private var handlerRef: EventHandlerRef?
    private var modifierTimer: Timer?
    private var pttModWasDown = false

    init(state: AppState) { self.state = state }

    func start() {
        // Register combo shortcuts via Carbon (no permissions needed)
        if let rec = state.recordingShortcut, !KeyCombo.isModifierKeyCode(rec.keyCode) {
            registerCarbonHotKey(
                keyCode: UInt32(rec.keyCode),
                modifiers: cgFlagsToCarbonMods(rec.modifiers),
                id: 1
            )
        }

        // For modifier-only shortcuts, poll flagsState (no permissions needed)
        if let ptt = state.pushToTalkShortcut, KeyCombo.isModifierKeyCode(ptt.keyCode) {
            startModifierPolling()
        } else if let ptt = state.pushToTalkShortcut {
            // PTT is a combo key, register via Carbon
            registerCarbonHotKey(
                keyCode: UInt32(ptt.keyCode),
                modifiers: cgFlagsToCarbonMods(ptt.modifiers),
                id: 2
            )
        }

        // Escape (keyCode 53) always cancels — matches superwhisper behavior
        registerCarbonHotKey(keyCode: 53, modifiers: 0, id: 3)

    }

    func stop() {
        modifierTimer?.invalidate()
        modifierTimer = nil
        modifierDispatch?.cancel()
        modifierDispatch = nil
        for ref in hotKeyRefs {
            if let ref { UnregisterEventHotKey(ref) }
        }
        hotKeyRefs.removeAll()
        if let h = handlerRef { RemoveEventHandler(h) }
        handlerRef = nil
    }

    // MARK: - Carbon Hot Key Registration (no permissions needed)

    private func registerCarbonHotKey(keyCode: UInt32, modifiers: UInt32, id: UInt32) {
        // Install handler if not already installed
        if handlerRef == nil {
            var eventTypes = [
                EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
                EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased))
            ]

            let selfPtr = Unmanaged.passUnretained(self).toOpaque()
            var handler: EventHandlerRef?

            InstallEventHandler(
                GetApplicationEventTarget(),
                { _, event, userData -> OSStatus in
                    guard let userData, let event else { return OSStatus(eventNotHandledErr) }
                    let monitor = Unmanaged<HotkeyMonitor>.fromOpaque(userData).takeUnretainedValue()

                    var hotKeyID = EventHotKeyID()
                    GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                    EventParamType(typeEventHotKeyID), nil,
                                    MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)

                    let kind = GetEventKind(event)
                    let pressed = kind == UInt32(kEventHotKeyPressed)
                    monitor.handleCarbonHotKey(id: hotKeyID.id, pressed: pressed)
                    return noErr
                },
                2, &eventTypes, selfPtr, &handler
            )
            handlerRef = handler
        }

        let hotKeyID = EventHotKeyID(signature: OSType(0x57474854), id: id) // "WGHT"
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(keyCode, modifiers, hotKeyID,
                                        GetApplicationEventTarget(), 0, &ref)
        if status == noErr {
            hotKeyRefs.append(ref)
        } else {
        }
    }

    private func handleCarbonHotKey(id: UInt32, pressed: Bool) {

        if id == 1 {
            // Toggle Recording
            if pressed {
                DispatchQueue.main.async {
                    self.state.isRecordingToggled.toggle()
                    if self.state.isRecordingToggled {
                        self.state.engine?.engageGate()
                    } else {
                        self.state.engine?.disengageGate()
                        self.state.isPTTHeld = false
                    }
                }
            }
        } else if id == 2 {
            // PTT (combo key version)
            DispatchQueue.main.async {
                if pressed && !self.state.isPTTHeld {
                    self.state.isPTTHeld = true
                    self.state.engine?.engageGate()
                } else if !pressed && self.state.isPTTHeld {
                    self.state.isPTTHeld = false
                    if !self.state.isRecordingToggled {
                        self.state.engine?.disengageGate()
                    }
                }
            }
        } else if id == 3 && pressed {
            // Escape — cancel everything
            DispatchQueue.main.async {
                self.state.isPTTHeld = false
                self.state.isRecordingToggled = false
                self.state.engine?.disengageGate()
            }
        }
    }

    // MARK: - Modifier Polling (for modifier-only shortcuts, no permissions needed)

    private func startModifierPolling() {
        // 4Hz polling on a background queue — minimal CPU
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        timer.schedule(deadline: .now(), repeating: .milliseconds(250))
        timer.setEventHandler { [weak self] in self?.pollModifiers() }
        timer.resume()
        modifierTimer = nil // store as dispatch source instead
        modifierDispatch = timer
    }
    private var modifierDispatch: DispatchSourceTimer?

    private func pollModifiers() {
        guard let ptt = state.pushToTalkShortcut else { return }

        let flags = CGEventSource.flagsState(.combinedSessionState)

        // Check device-dependent flags for left/right modifier distinction
        let isDown: Bool
        switch ptt.keyCode {
        case 61: isDown = (flags.rawValue & 0x40) != 0       // Right Option (NX_DEVICERALTKEYMASK)
        case 58: isDown = (flags.rawValue & 0x20) != 0       // Left Option (NX_DEVICELALTKEYMASK)
        case 60: isDown = (flags.rawValue & 0x04) != 0       // Right Shift (NX_DEVICERSHIFTKEYMASK)
        case 56: isDown = (flags.rawValue & 0x02) != 0       // Left Shift (NX_DEVICELSHIFTKEYMASK)
        case 55: isDown = (flags.rawValue & 0x08) != 0       // Left Command (NX_DEVICELCMDKEYMASK)
        case 54: isDown = (flags.rawValue & 0x10) != 0       // Right Command (NX_DEVICERCMDKEYMASK)
        case 59: isDown = (flags.rawValue & 0x01) != 0       // Left Control (NX_DEVICELCTLKEYMASK)
        case 62: isDown = (flags.rawValue & 0x2000) != 0     // Right Control (NX_DEVICERCTLKEYMASK)
        default:
            // Generic modifier check
            let req = CGEventFlags(rawValue: ptt.modifiers)
            isDown = flags.contains(req)
        }

        if isDown && !pttModWasDown {
            pttModWasDown = true
            DispatchQueue.main.async {
                self.state.isPTTHeld = true
                self.state.engine?.engageGate()
            }
        } else if !isDown && pttModWasDown {
            pttModWasDown = false
            DispatchQueue.main.async {
                self.state.isPTTHeld = false
                if !self.state.isRecordingToggled {
                    self.state.engine?.disengageGate()
                }
            }
        }
    }

    // MARK: - Helpers

    private func cgFlagsToCarbonMods(_ cgFlags: UInt64) -> UInt32 {
        var carbon: UInt32 = 0
        if cgFlags & CGEventFlags.maskCommand.rawValue != 0   { carbon |= UInt32(cmdKey) }
        if cgFlags & CGEventFlags.maskShift.rawValue != 0     { carbon |= UInt32(shiftKey) }
        if cgFlags & CGEventFlags.maskAlternate.rawValue != 0 { carbon |= UInt32(optionKey) }
        if cgFlags & CGEventFlags.maskControl.rawValue != 0   { carbon |= UInt32(controlKey) }
        return carbon
    }
}
