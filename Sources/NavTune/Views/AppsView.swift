import SwiftUI

/// Per-app sound: give an app its own preset and volume, leave it untouched, or mute it on the speakers.
struct AppsView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                StatusBanner()

                Card(title: tr("Âm thanh riêng cho từng ứng dụng", "Per-app sound"), icon: "square.stack.3d.up") {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(tr("Mặc định mọi ứng dụng dùng EQ chung. Anh có thể cho một ứng dụng dùng preset riêng (ví dụ Chrome nghe nhạc dùng V-Pop, Zoom dùng Podcast), hoặc chọn “Không xử lý” để giữ nguyên âm thanh gốc.",
                                "By default every app uses the main EQ. Give an app its own preset (e.g. Chrome for music on V-Pop, Zoom on Podcast), or choose “Untouched” to keep its original sound."))
                        Label {
                            Text(LocalizedStringKey(tr("**Tắt tiếng**: ứng dụng vẫn chạy và vẫn phát như bình thường, chỉ là không ra loa, tai nghe của anh. Livestream (OBS) và phần mềm ghi màn hình vẫn thu được tiếng.",
                                    "**Mute**: the app keeps running and playing as usual, it just isn't heard on your speakers or headphones. Livestreams (OBS) and screen recorders still capture its sound.")))
                        } icon: {
                            Image(systemName: "speaker.slash.fill").foregroundStyle(.red)
                        }
                    }
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                }

                let entries = state.appEntries
                if entries.isEmpty {
                    Card {
                        VStack(spacing: 8) {
                            Image(systemName: "music.note.house").font(.largeTitle).foregroundStyle(.secondary)
                            Text(tr("Chưa có ứng dụng nào dùng âm thanh. Mở YouTube, Spotify… rồi quay lại đây.",
                                    "No apps are using audio yet. Open YouTube, Spotify… and come back here."))
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.center)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 20)
                    }
                } else {
                    VStack(spacing: 10) {
                        ForEach(entries) { app in
                            AppRow(app: app)
                        }
                    }
                }

                Text(tr("Safari phát âm thanh qua tiến trình WebKit dùng chung, nên cài đặt cho Safari cũng áp dụng cho các ứng dụng dùng WebKit khác.",
                        "Safari plays audio through a shared WebKit process, so Safari's setting also applies to other WebKit-based apps."))
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(20)
        }
        .disabled(!state.isOn)
    }
}

private struct AppRow: View {
    @Environment(AppState.self) private var state
    let app: AudioApp

    private var rule: AppRule? { state.rule(for: app.bundleID) }
    private var muted: Bool { rule?.mode == .mute }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(nsImage: app.icon)
                    .resizable()
                    .frame(width: 36, height: 36)
                    .opacity(muted ? 0.45 : 1)
                    .overlay(alignment: .bottomTrailing) {
                        if muted {
                            Image(systemName: "speaker.slash.fill")
                                .font(.system(size: 9, weight: .bold))
                                .foregroundStyle(.white)
                                .padding(3)
                                .background(Circle().fill(.red))
                                .offset(x: 3, y: 3)
                        }
                    }
                VStack(alignment: .leading, spacing: 2) {
                    Text(app.name).font(.headline)
                    status
                }
                Spacer()
                MuteButton(muted: muted) { state.setMuted(!muted, app: app) }
                Picker("", selection: Binding(get: { rule?.mode }, set: { state.setRuleMode($0, for: app) })) {
                    Text(tr("Dùng EQ chung", "Main EQ")).tag(AppRule.Mode?.none)
                    Text(tr("Preset riêng", "Own preset")).tag(Optional(AppRule.Mode.preset))
                    Text(tr("Không xử lý", "Untouched")).tag(Optional(AppRule.Mode.bypass))
                    Divider()
                    Text(tr("Tắt tiếng ở loa", "Muted on speakers")).tag(Optional(AppRule.Mode.mute))
                }
                .labelsHidden()
                .frame(width: 170)
            }

            if let rule, rule.mode == .preset {
                HStack(spacing: 16) {
                    Picker(tr("Preset", "Preset"), selection: Binding(
                        get: { rule.presetID },
                        set: { id in var r = rule; r.presetID = id; state.setRule(r) })) {
                        ForEach(state.allPresets) { Label($0.displayName, systemImage: $0.icon).tag($0.id) }
                    }
                    .frame(maxWidth: 260)
                    Text(tr("Âm lượng", "Volume"))
                    Slider(value: Binding(
                        get: { Double(rule.volume) },
                        set: { v in var r = rule; r.volume = (Float(v) * 2).rounded() / 2; state.setRule(r) }),
                           in: -12...12)
                    ValuePill(text: Format.dB(rule.volume))
                }
                .padding(.leading, 48)
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 12).fill(.background.secondary))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(muted ? Color.red.opacity(0.35) : Color.primary.opacity(0.07)))
    }

    @ViewBuilder
    private var status: some View {
        if muted {
            Label(app.isPlaying ? tr("Đang phát, đã tắt tiếng ở loa", "Playing, muted on speakers")
                                : tr("Đã tắt tiếng ở loa", "Muted on speakers"),
                  systemImage: "speaker.slash.fill")
                .font(.caption)
                .foregroundStyle(.red)
        } else if app.isPlaying {
            Label(tr("Đang phát", "Playing"), systemImage: "waveform")
                .symbolEffect(.variableColor.iterative, isActive: true)
                .font(.caption)
                .foregroundStyle(.green)
        } else {
            Text(app.isRunning ? tr("Đang mở", "Running") : tr("Chưa mở", "Not running"))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

/// One-click mute on the speakers; the app itself keeps playing.
struct MuteButton: View {
    let muted: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                .contentTransition(.symbolEffect(.replace))
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(muted ? Color.white : Color.primary)
                .frame(width: 30, height: 22)
                .background(RoundedRectangle(cornerRadius: 6)
                    .fill(muted ? AnyShapeStyle(Color.red) : AnyShapeStyle(Color.secondary.opacity(hovering ? 0.22 : 0.12))))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(muted ? tr("Bật lại tiếng ở loa", "Unmute on speakers")
                    : tr("Tắt tiếng ở loa. Ứng dụng vẫn chạy, livestream và ghi hình vẫn có tiếng.",
                         "Mute on speakers. The app keeps running; livestreams and recordings still get its sound."))
    }
}
