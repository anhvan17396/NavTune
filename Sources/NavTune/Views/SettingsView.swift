import SwiftUI

struct SettingsView: View {
    @Environment(AppState.self) private var state
    @State private var confirmingReset = false

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.1"
    }

    var body: some View {
        @Bindable var state = state
        Form {
            Section(tr("Chung", "General")) {
                Picker(tr("Ngôn ngữ", "Language"), selection: $state.language) {
                    ForEach(AppLanguage.allCases) { Text($0.title).tag($0) }
                }
                Toggle(tr("Khởi động cùng máy", "Launch at login"), isOn: Binding(get: { state.launchAtLogin },
                                                                                  set: { state.setLaunchAtLogin($0) }))
                Toggle(tr("Hiện biểu tượng trên Dock", "Show in Dock"), isOn: $state.showInDock)
            }

            Section {
                LabeledContent(tr("Ghi âm thanh hệ thống", "System audio recording")) {
                    Button(tr("Mở Cài đặt quyền riêng tư", "Open Privacy Settings")) { state.openPrivacySettings() }
                }
            } header: {
                Text(tr("Quyền truy cập", "Permissions"))
            } footer: {
                Text(tr("NavTune cần quyền này để nhận âm thanh từ YouTube, trình duyệt và các app khác. Âm thanh chỉ được xử lý ngay trên máy, không ghi lại và không gửi đi đâu.",
                        "NavTune needs this to receive sound from YouTube, browsers and other apps. Audio is processed only on your Mac — never recorded or sent anywhere."))
            }

            Section(tr("Dữ liệu", "Data")) {
                Button(tr("Xem lại hướng dẫn ban đầu", "Show the welcome guide again")) { state.hasOnboarded = false }
                Button(tr("Khôi phục cài đặt âm thanh mặc định…", "Reset sound settings…"), role: .destructive) {
                    confirmingReset = true
                }
            }

            Section(tr("Giới thiệu", "About")) {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("NavTune").font(.title3.bold())
                        Text(tr("Phiên bản \(version)", "Version \(version)")).foregroundStyle(.secondary)
                        Text(tr("Ứng dụng miễn phí thuộc hệ sinh thái navtools.vn.", "A free app from the navtools.vn family."))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Link("navtools.vn", destination: URL(string: "https://navtools.vn")!)
                }
            }
        }
        .formStyle(.grouped)
        .confirmationDialog(tr("Khôi phục cài đặt âm thanh?", "Reset sound settings?"), isPresented: $confirmingReset) {
            Button(tr("Khôi phục", "Reset"), role: .destructive) { state.resetAllSound() }
        } message: {
            Text(tr("EQ và hiệu ứng của mọi thiết bị sẽ về mặc định. Preset của anh vẫn được giữ.",
                    "EQ and effects for every device go back to default. Your presets are kept."))
        }
    }
}
