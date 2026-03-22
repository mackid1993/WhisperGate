import SwiftUI
import AVFoundation
import AppKit

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
            Image(nsImage: Self.menuBarImage(state.menuBarIcon, color: NSColor(state.menuBarIconColor)))
        }
        .menuBarExtraStyle(.window)
    }

    static func menuBarImage(_ symbolName: String, color: NSColor) -> NSImage {
        let size = NSSize(width: 22, height: 22)
        let img = NSImage(size: size, flipped: false) { rect in
            guard let symbol = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: 14, weight: .regular))?
                .withSymbolConfiguration(.init(paletteColors: [color])) else { return false }
            let symbolSize = symbol.size
            let x = round((rect.width - symbolSize.width) / 2) + 2
            let y = round((rect.height - symbolSize.height) / 2)
            symbol.draw(in: NSRect(x: x, y: y, width: symbolSize.width, height: symbolSize.height))
            return true
        }
        img.isTemplate = false
        img.alignmentRect = NSRect(origin: .zero, size: size)
        return img
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
