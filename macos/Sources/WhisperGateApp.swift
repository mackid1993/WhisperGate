import SwiftUI
import AVFoundation

@main
struct WhisperGateApp: App {
    @State private var state = AppState.shared

    var body: some Scene {
        MenuBarExtra {
            if state.needsSetup {
                SetupView {
                    state.needsSetup = false
                    UserDefaults.standard.set(true, forKey: "setupComplete")
                    state.setup()
                }
            } else {
                PopoverView()
                    .environment(state)
            }
        } label: {
            Image(systemName: state.needsSetup ? "exclamationmark.circle" : state.menuBarIcon)
        }
        .menuBarExtraStyle(.window)
    }

    init() {
        let micOK = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        let setupDone = UserDefaults.standard.bool(forKey: "setupComplete")
        let hasShortcuts = AppState.shared.pushToTalkShortcut != nil || AppState.shared.recordingShortcut != nil


        if !micOK || !setupDone || !hasShortcuts {
            AppState.shared.needsSetup = true
        } else {
            AppState.shared.needsSetup = false
            DispatchQueue.main.async { AppState.shared.setup() }
        }
    }
}
