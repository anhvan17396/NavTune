import AppKit
import SwiftUI

struct MenuBarView: View {
    @Environment(AppState.self) private var state
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var state = state
        VStack(alignment: .leading, spacing: 12) {
            header

            if let error = state.errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            } else if state.noSignal {
                Label(tr("Chưa nhận được âm thanh. Kiểm tra quyền trong Cài đặt.", "No audio yet. Check the permission in Settings."),
                      systemImage: "ear.trianglebadge.exclamationmark")
                    .font(.caption)
                    .foregroundStyle(.yellow)
            }

            FrequencyGraph(settings: state.settings, sampleRate: state.sampleRate, showsGrid: false, cornerRadius: 8)
                .frame(height: 70)
                .overlay(alignment: .topLeading) {
                    Text(state.currentPresetName)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white)
                        .padding(6)
                }

            Group {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(state.allPresets) { preset in
                            let selected = state.settings.presetID == preset.id
                            Button {
                                state.apply(preset)
                            } label: {
                                Label(preset.displayName, systemImage: preset.icon)
                                    .font(.caption)
                                    .padding(.horizontal, 9)
                                    .padding(.vertical, 5)
                                    .background(Capsule().fill(selected ? AnyShapeStyle(Brand.gradient)
                                                               : AnyShapeStyle(Color.secondary.opacity(0.12))))
                                    .foregroundStyle(selected ? Color.white : Color.primary)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }

                Toggle(tr("Tự chọn preset theo nội dung", "Auto preset by content"), isOn: $state.autoPreset)
                    .font(.callout)

                Picker(tr("Thiết bị", "Device"), selection: $state.preferredDeviceUID) {
                    Text(tr("Mặc định của macOS", "macOS default")).tag(String?.none)
                    ForEach(state.devices) { device in
                        Label(device.name, systemImage: device.icon).tag(Optional(device.uid))
                    }
                }

                ValueSlider(title: tr("Bass", "Bass"), icon: "speaker.wave.3",
                            value: $state.settings.bass, range: -12...12, compact: true, format: Format.dB)
                ValueSlider(title: tr("Tăng âm lượng", "Volume boost"), icon: "speaker.plus",
                            value: $state.settings.boost, range: 0...12, compact: true, format: Format.dB)

                Toggle(tr("Nghe bản gốc (so sánh)", "Original sound (compare)"), isOn: $state.bypass)
                    .font(.callout)

                quickMute
            }
            .disabled(!state.isOn)

            Divider()

            HStack {
                Button(tr("Mở NavTune", "Open NavTune")) {
                    openWindow(id: "main")
                    NSApp.activate()
                }
                .keyboardShortcut(",")
                Spacer()
                Button(tr("Thoát", "Quit")) {
                    state.shutdown()
                    NSApp.terminate(nil)
                }
                .keyboardShortcut("q")
            }
        }
        .padding(16)
        .frame(width: 340)
        .tint(Brand.accent)
    }

    /// Apps playing now (and those muted), one click to silence them on the speakers.
    @ViewBuilder
    private var quickMute: some View {
        let apps = state.quickMuteApps
        if !apps.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text(tr("Tắt tiếng ở loa (app vẫn chạy, livestream vẫn có tiếng)",
                        "Mute on speakers (apps keep running, streams keep the sound)"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(apps) { QuickMuteChip(app: $0) }
                    }
                }
            }
        }
    }

    private var header: some View {
        @Bindable var state = state
        return HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 0) {
                Text("NavTune").font(.headline)
                HStack(spacing: 4) {
                    Image(systemName: state.activeDevice?.icon ?? "speaker.slash")
                    Text(state.activeDevice?.name ?? tr("Không có thiết bị", "No device")).lineLimit(1)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer()
            Toggle("", isOn: $state.isOn).toggleStyle(.switch).labelsHidden()
        }
    }
}

private struct QuickMuteChip: View {
    @Environment(AppState.self) private var state
    let app: AudioApp

    var body: some View {
        let muted = state.isMuted(app.bundleID)
        Button {
            state.setMuted(!muted, app: app)
        } label: {
            HStack(spacing: 5) {
                Image(nsImage: app.icon)
                    .resizable()
                    .frame(width: 16, height: 16)
                    .opacity(muted ? 0.5 : 1)
                Text(app.name).lineLimit(1)
                Image(systemName: muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                    .font(.system(size: 10, weight: .semibold))
                    .contentTransition(.symbolEffect(.replace))
            }
            .font(.caption)
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(Capsule().fill(muted ? Color.red.opacity(0.18) : Color.secondary.opacity(0.12)))
            .foregroundStyle(muted ? Color.red : Color.primary)
        }
        .buttonStyle(.plain)
        .help(muted ? tr("Bật lại tiếng \(app.name) ở loa", "Unmute \(app.name) on speakers")
                    : tr("Tắt tiếng \(app.name) ở loa", "Mute \(app.name) on speakers"))
    }
}
