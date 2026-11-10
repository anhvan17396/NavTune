import SwiftUI

struct OnboardingView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        @Bindable var state = state
        VStack(spacing: 22) {
            HStack {
                Spacer()
                Picker("", selection: $state.language) {
                    Text("Tiếng Việt").tag(AppLanguage.vietnamese)
                    Text("English").tag(AppLanguage.english)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 200)
            }

            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 96, height: 96)
                .shadow(color: Brand.accent.opacity(0.4), radius: 16, y: 6)

            VStack(spacing: 6) {
                Text(tr("Chào mừng đến với NavTune", "Welcome to NavTune")).font(.largeTitle.bold())
                Text(tr("Âm thanh hay hơn cho mọi thứ anh nghe trên Mac.", "Better sound for everything you hear on your Mac."))
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 14) {
                feature("slider.vertical.3", tr("EQ đồ hoạ, EQ tham số và 24 preset", "Graphic & parametric EQ, 24 presets"),
                        tr("Bolero, V-Pop, Rock, EDM, Jazz… hoặc để NavTune tự chọn theo nội dung.",
                           "Bolero, V-Pop, Rock, EDM, Jazz… or let NavTune pick by content."))
                feature("sparkles", tr("Hiệu ứng nâng cao", "Advanced effects"),
                        tr("Bass, độ trong, không gian 3D, cân bằng và tăng âm lượng.",
                           "Bass, clarity, 3D space, volume leveler and boost."))
                feature("square.stack.3d.up", tr("Riêng cho từng app và thiết bị", "Per app and per device"),
                        tr("Chrome một kiểu, Spotify một kiểu; AirPods và loa nhớ cài đặt riêng.",
                           "Chrome one way, Spotify another; AirPods and speakers remember their own settings."))
            }
            .frame(maxWidth: 440, alignment: .leading)

            GroupBox {
                Label {
                    Text(LocalizedStringKey(tr(
                        "Ở bước tiếp theo, macOS sẽ hỏi quyền **ghi âm thanh hệ thống**. Anh bấm **Cho phép** để NavTune nhận được âm thanh. Âm thanh chỉ xử lý trên máy, không ghi lại.",
                        "Next, macOS will ask for **system audio recording** permission. Click **Allow** so NavTune can receive sound. Audio is processed only on your Mac and never recorded.")))
                } icon: {
                    Image(systemName: "lock.shield").foregroundStyle(Brand.accent)
                }
                .font(.callout)
                .padding(6)
            }
            .frame(maxWidth: 460)

            Button {
                state.completeOnboarding()
            } label: {
                Text(tr("Bắt đầu sử dụng", "Get started"))
                    .font(.headline)
                    .foregroundStyle(.white)
                    .frame(minWidth: 200)
                    .padding(.vertical, 10)
                    .background(Capsule().fill(Brand.gradient))
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.defaultAction)
        }
        .padding(32)
        .frame(width: 580)
    }

    private func feature(_ icon: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            IconBadge(symbol: icon, size: 34)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(detail).foregroundStyle(.secondary)
            }
        }
    }
}
