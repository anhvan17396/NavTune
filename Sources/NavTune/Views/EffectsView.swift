import SwiftUI

struct EffectsView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        @Bindable var state = state
        ScrollView {
            VStack(spacing: 16) {
                StatusBanner()

                LazyVGrid(columns: [GridItem(.flexible(), spacing: 16), GridItem(.flexible(), spacing: 16)], spacing: 16) {
                    Card {
                        ValueSlider(title: tr("Bass", "Bass"), icon: "speaker.wave.3",
                                    detail: tr("Tăng hoặc giảm âm trầm, dưới khoảng 90 Hz.", "Boost or cut the low end, below about 90 Hz."),
                                    value: $state.settings.bass, range: -12...12, format: Format.dB)
                    }
                    Card {
                        ValueSlider(title: tr("Độ trong", "Clarity"), icon: "sparkles",
                                    detail: tr("Làm tiếng sáng và chi tiết hơn (trên 9 kHz).", "Brighter, more detailed highs (above 9 kHz)."),
                                    value: $state.settings.clarity, range: -12...12, format: Format.dB)
                    }
                    Card {
                        ValueSlider(title: tr("Không gian 3D", "3D Space"), icon: "person.2.wave.2",
                                    detail: tr("Mở rộng âm trường stereo. 100% là gốc, 0% là mono.", "Widens the stereo image. 100% is original, 0% is mono."),
                                    value: $state.settings.width, range: 0...2, step: 0.05, format: Format.percent)
                    }
                    Card {
                        ValueSlider(title: tr("Cân bằng âm lượng", "Volume leveler"), icon: "dial.medium",
                                    detail: tr("Giảm chênh lệch to nhỏ giữa các bài hát và video.", "Evens out loud and quiet songs and videos."),
                                    value: $state.settings.loudness, range: 0...1, step: 0.05, format: Format.percent)
                    }
                    Card {
                        ValueSlider(title: tr("Tăng âm lượng", "Volume boost"), icon: "speaker.plus",
                                    detail: tr("Tăng to hơn mức tối đa của máy, có bộ chống vỡ tiếng.", "Louder than the system maximum, with clipping protection."),
                                    value: $state.settings.boost, range: 0...12, format: Format.dB)
                    }
                    Card {
                        VStack(alignment: .leading, spacing: 10) {
                            HStack(spacing: 10) {
                                IconBadge(symbol: "lightbulb")
                                Text(tr("Gợi ý", "Tips")).font(.headline)
                            }
                            Text(tr("Tai nghe Bluetooth: Bass +3, Độ trong +2, Không gian 120%.\nXem YouTube nhiều kênh: bật Cân bằng âm lượng 50%.",
                                    "Bluetooth headphones: Bass +3, Clarity +2, 3D Space 120%.\nWatching many YouTube channels: Volume leveler 50%."))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Spacer(minLength: 0)
                            Button(tr("Đặt lại hiệu ứng", "Reset effects"), systemImage: "arrow.counterclockwise") {
                                state.resetEffects()
                            }
                        }
                    }
                }
            }
            .padding(20)
        }
        .disabled(!state.isOn)
    }
}
