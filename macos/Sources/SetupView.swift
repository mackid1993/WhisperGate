import SwiftUI
import AVFoundation

struct SetupView: View {
    @State private var micGranted = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    @State private var ptt: KeyCombo? = SuperWhisperIntegration.readPushToTalk()
    @State private var rec: KeyCombo? = SuperWhisperIntegration.readToggleRecording()
    var onComplete: () -> Void

    var body: some View {
        VStack(spacing: 20) {
            // Header
            VStack(spacing: 8) {
                Image(systemName: "waveform.and.mic")
                    .font(.system(size: 36))
                    .foregroundStyle(.blue)
                Text("WhisperGate")
                    .font(.title.bold())
                Text("Noise gate for superwhisper")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            Divider()

            // How it works
            VStack(alignment: .leading, spacing: 8) {
                Text("How it works").font(.headline)
                step("keyboard", "Detects your superwhisper hotkey")
                step("mic.fill", "Monitors your mic while you dictate")
                step("waveform.badge.minus", "Reduces mic level when you're not speaking to filter background noise")
                step("waveform", "Restores full volume when you speak — your voice comes through clearly")
                step("stop.circle", "Returns mic to normal when you stop dictating")
            }

            Divider()

            // Step 1: Mic permission
            VStack(alignment: .leading, spacing: 8) {
                Text("Step 1: Microphone").font(.headline)
                if micGranted {
                    Label("Microphone access granted", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                } else {
                    Text("WhisperGate needs mic access to read audio levels.")
                        .font(.callout).foregroundStyle(.secondary)
                    Button("Grant Microphone Access") {
                        AVCaptureDevice.requestAccess(for: .audio) { ok in
                            DispatchQueue.main.async { micGranted = ok }
                        }
                    }
                    .buttonStyle(.bordered)
                }
            }

            Divider()

            // Step 2: Shortcuts
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Step 2: superwhisper Shortcuts").font(.headline)
                    Spacer()
                    Button("Refresh") {
                        ptt = SuperWhisperIntegration.readPushToTalk()
                        rec = SuperWhisperIntegration.readToggleRecording()
                    }
                    .controlSize(.small)
                    .buttonStyle(.bordered)
                }

                if ptt != nil || rec != nil {
                    if let ptt {
                        shortcutRow("Push to Talk", ptt.displayString)
                    }
                    if let rec {
                        shortcutRow("Toggle Recording", rec.displayString)
                    }
                    Label("Synced from superwhisper", systemImage: "checkmark.circle.fill")
                        .font(.caption).foregroundStyle(.green)
                } else {
                    Text("No shortcuts detected. Open superwhisper, set your shortcuts, then tap Refresh.")
                        .font(.callout).foregroundStyle(.orange)
                }
            }

            Spacer(minLength: 8)

            // Start
            Button(action: {
                if let ptt { AppState.shared.pushToTalkShortcut = ptt }
                if let rec { AppState.shared.recordingShortcut = rec }
                onComplete()
            }) {
                Text("Start WhisperGate")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
        }
        .padding(28)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func step(_ icon: String, _ text: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).frame(width: 22).foregroundStyle(.blue)
            Text(text).font(.callout)
        }
    }

    private func shortcutRow(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).font(.callout)
            Spacer()
            Text(value)
                .font(.system(.callout, design: .monospaced))
                .padding(.horizontal, 8).padding(.vertical, 3)
                .background(RoundedRectangle(cornerRadius: 6).fill(.blue.opacity(0.15)))
        }
    }
}
