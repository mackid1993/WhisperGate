import SwiftUI

struct PopoverView: View {
    @Environment(AppState.self) var state
    @State private var isVisible = false

    var body: some View {
        Group {
            if isVisible {
                popoverContent
            } else {
                Color.clear.frame(width: 340, height: 100)
            }
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
    }

    private var popoverContent: some View {
        @Bindable var state = state
        return VStack(spacing: 0) {
            headerSection
            Divider()
            ScrollView {
                VStack(spacing: 16) {
                    statusSection
                    gateControlsSection
                    hotkeySection
                    settingsSection
                    Divider()
                    Button(action: {
                        state.teardown()
                        NSApplication.shared.terminate(nil)
                    }) {
                        HStack {
                            Image(systemName: "power")
                            Text("Quit WhisperGate")
                        }
                        .frame(maxWidth: .infinity)
                    }
                    .controlSize(.small)
                    .buttonStyle(.bordered)
                }
                .padding(16)
            }
        }
        .frame(width: 340, height: 720)
    }

    // MARK: - Header

    private var headerSection: some View {
        @Bindable var state = state
        return HStack {
            Image(systemName: state.menuBarIcon)
                .font(.title2)
                .foregroundStyle(state.statusColor)

            VStack(alignment: .leading, spacing: 2) {
                Text("WhisperGate")
                    .font(.headline)
                Text(state.statusText)
                    .font(.caption)
                    .foregroundStyle(state.statusColor)
            }

            Spacer()

            Toggle("", isOn: $state.isEnabled)
                .toggleStyle(.switch)
                .controlSize(.small)
        }
        .padding(16)
    }

    // MARK: - Status

    private var statusSection: some View {
        VStack(spacing: 8) {
            HStack(spacing: 12) {
                Circle()
                    .fill(state.statusColor.opacity(0.2))
                    .frame(width: 36, height: 36)
                    .overlay(
                        Image(systemName: state.isGateEngaged ? (state.isGateOpen ? "waveform" : "waveform.slash") : "mic.fill")
                            .foregroundStyle(state.statusColor)
                    )

                VStack(alignment: .leading, spacing: 2) {
                    Text(state.isGateEngaged ? (state.isGateOpen ? "Full Volume" : "Mic Muted") : "Standby")
                        .font(.system(.body, weight: .medium))
                    Text(state.isGateEngaged ? (state.isGateOpen ? "Your voice is passing through" : "Mic muted — listening for speech") : "Waiting for superwhisper hotkey")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }

            // Level meter
            GeometryReader { geo in
                let w = geo.size.width
                let level = CGFloat(max(0, min(1, (state.currentLevelSmoothed + 80) / 80)))
                let thresh = CGFloat(max(0, min(1, (state.threshold + 80) / 80)))

                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 3).fill(.quaternary)
                    RoundedRectangle(cornerRadius: 3)
                        .fill(state.isGateEngaged && !state.isGateOpen ? Color.orange : Color.green)
                        .frame(width: max(0, w * level))
                    Rectangle().fill(.white.opacity(0.7))
                        .frame(width: 2)
                        .offset(x: w * thresh - 1)
                }
            }
            .frame(height: 10)

            HStack {
                Text(String(format: "%.0f dB", state.currentLevelSmoothed))
                    .font(.system(.caption2, design: .monospaced))
                    .foregroundStyle(.secondary)
                Spacer()
                if state.isGateEngaged {
                    Text(state.isGateOpen ? "OPEN" : "GATING")
                        .font(.system(.caption2, design: .monospaced, weight: .bold))
                        .foregroundStyle(state.isGateOpen ? .green : .orange)
                }
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(.quaternary))
    }

    // MARK: - Gate Controls

    private var gateControlsSection: some View {
        @Bindable var state = state
        return VStack(alignment: .leading, spacing: 8) {
            Text("Noise Gate").font(.subheadline.weight(.semibold))

            HStack {
                Text("Threshold").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.0f dB", state.threshold))
                    .font(.system(.caption, design: .monospaced))
            }
            Slider(value: $state.threshold, in: -60...(-20), step: 1)
            Text("Set just above your room noise level.")
                .font(.system(size: 9)).foregroundStyle(.tertiary)

            if !state.virtualMicEnabled {
                HStack {
                    Text("Gated Volume").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Text(String(format: "%.0f%%", state.reductionPercent))
                        .font(.system(.caption, design: .monospaced))
                }
                Slider(value: $state.reductionPercent, in: 0...100, step: 5)
                Text("Mic volume when not speaking. 0% = fully silenced. 100% = no reduction.")
                    .font(.system(size: 9)).foregroundStyle(.tertiary)
            }
        }
    }

    // MARK: - Shortcuts

    private var hotkeySection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("superwhisper Shortcuts").font(.subheadline.weight(.semibold))
                Spacer()
                Button(action: { state.syncFromSuperWhisper() }) {
                    Label("Sync", systemImage: "arrow.triangle.2.circlepath").font(.caption)
                }
                .controlSize(.small).buttonStyle(.bordered)
            }

            shortcutRow("Push to Talk", "Hold to record", state.pushToTalkShortcut)
            shortcutRow("Toggle Recording", "Press to start/stop", state.recordingShortcut)
        }
    }

    private func shortcutRow(_ label: String, _ sub: String, _ combo: KeyCombo?) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(label).font(.caption.weight(.medium))
                Text(sub).font(.system(size: 9)).foregroundStyle(.tertiary)
            }
            Spacer()
            Text(combo?.displayString ?? "Not Set")
                .font(.system(.caption, design: .monospaced))
                .padding(.horizontal, 8).padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6).fill(combo != nil ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.1)))
        }
    }

    // MARK: - Settings

    private var settingsSection: some View {
        @Bindable var state = state
        return VStack(alignment: .leading, spacing: 8) {
            Text("Settings").font(.subheadline.weight(.semibold))

            Picker("Input Device", selection: $state.inputDeviceUID) {
                Text("System Default").tag(nil as String?)
                ForEach(AudioDeviceManager.inputDevices().filter { $0.uid != "com.whispergate.virtualmic" }) { device in
                    Text(device.name).tag(device.uid as String?)
                }
            }
            .pickerStyle(.menu).controlSize(.small)

            Toggle("Start at Login", isOn: $state.startAtLogin).controlSize(.small)

            Toggle("Virtual Mic Driver", isOn: $state.virtualMicEnabled).controlSize(.small)
            if state.virtualMicEnabled {
                VStack(alignment: .leading, spacing: 4) {
                    Label("Installed", systemImage: "checkmark.circle.fill")
                        .font(.caption).foregroundStyle(.green)
                    Text("Select **\"WhisperGate Mic\"** as your input device in superwhisper for true silence when gated.")
                        .font(.system(size: 9)).foregroundStyle(.secondary)
                    Text("Driver location: /Library/Audio/Plug-Ins/HAL/WhisperGateAudio.driver")
                        .font(.system(size: 8, design: .monospaced)).foregroundStyle(.tertiary)
                }
            } else {
                Text("Installs a lightweight audio driver to /Library/Audio/Plug-Ins/HAL/ that creates a virtual mic with true silence when gated. Removes cleanly when toggled off.")
                    .font(.system(size: 9)).foregroundStyle(.tertiary)
            }

            if let error = state.errorMessage {
                Label(error, systemImage: "xmark.circle.fill")
                    .font(.caption).foregroundStyle(.red)
            }
        }
    }
}
