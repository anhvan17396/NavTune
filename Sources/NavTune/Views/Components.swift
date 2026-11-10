import AppKit
import SwiftUI

enum Brand {
    static let accent = Color(red: 0.38, green: 0.36, blue: 0.98)
    static let gradient = LinearGradient(
        colors: [Color(red: 0.02, green: 0.62, blue: 0.98),
                 Color(red: 0.36, green: 0.27, blue: 0.96),
                 Color(red: 0.62, green: 0.16, blue: 0.86)],
        startPoint: .topLeading, endPoint: .bottomTrailing)
    static let graphBackground = LinearGradient(
        colors: [Color(red: 0.06, green: 0.07, blue: 0.14), Color(red: 0.11, green: 0.07, blue: 0.20)],
        startPoint: .top, endPoint: .bottom)
}

enum Format {
    static func dB(_ v: Float) -> String { String(format: "%+.1f dB", v) }
    static func percent(_ v: Float) -> String { "\(Int((v * 100).rounded()))%" }
    static func frequency(_ f: Double) -> String {
        f >= 1000 ? "\(Int(f / 1000))k" : "\(Int(f))"
    }
    static func hertz(_ f: Double) -> String {
        f >= 1000 ? String(format: f >= 10000 ? "%.1f kHz" : "%.2f kHz", f / 1000) : "\(Int(f.rounded())) Hz"
    }
}

/// Gradient rounded square holding an SF Symbol, used as section and row icons.
struct IconBadge: View {
    let symbol: String
    var size: CGFloat = 26

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.48, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(RoundedRectangle(cornerRadius: size * 0.28).fill(Brand.gradient))
    }
}

struct Card<Content: View>: View {
    var title: String?
    var icon: String?
    var trailing: AnyView?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let title {
                HStack(spacing: 10) {
                    if let icon { IconBadge(symbol: icon) }
                    Text(title).font(.headline)
                    Spacer()
                    trailing
                }
            }
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(.background.secondary))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.07)))
    }
}

/// Small rounded label for values like "+3.0 dB".
struct ValuePill: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.callout.monospacedDigit().weight(.medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Capsule().fill(Brand.accent.opacity(0.14)))
            .foregroundStyle(Brand.accent)
    }
}

struct ValueSlider: View {
    let title: String
    let icon: String
    var detail: String?
    @Binding var value: Float
    let range: ClosedRange<Float>
    var step: Float = 0.5
    var compact = false
    let format: (Float) -> String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                if compact {
                    Image(systemName: icon).foregroundStyle(Brand.accent).frame(width: 18)
                } else {
                    IconBadge(symbol: icon)
                }
                Text(title).font(compact ? .callout : .headline)
                Spacer()
                ValuePill(text: format(value))
            }
            Slider(value: Binding(get: { Double(value) },
                                  set: { value = (Float($0) / step).rounded() * step }),
                   in: Double(range.lowerBound)...Double(range.upperBound))
            if let detail {
                Text(detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

struct VerticalSlider: View {
    @Binding var value: Float
    var range: ClosedRange<Float> = -12...12
    @State private var dragging = false

    var body: some View {
        GeometryReader { geo in
            let height = geo.size.height
            let t = CGFloat((value - range.lowerBound) / (range.upperBound - range.lowerBound))
            let y = height * (1 - t)
            let mid = height / 2

            ZStack(alignment: .top) {
                Capsule().fill(Color.secondary.opacity(0.18)).frame(width: 6)
                Rectangle().fill(Color.secondary.opacity(0.45)).frame(width: 16, height: 1).offset(y: mid)
                Capsule().fill(Brand.gradient)
                    .frame(width: 6, height: abs(y - mid))
                    .offset(y: min(y, mid))
                Circle().fill(.white)
                    .overlay(Circle().strokeBorder(Brand.accent.opacity(dragging ? 0.9 : 0.25), lineWidth: 2))
                    .shadow(color: .black.opacity(0.3), radius: 2, y: 1)
                    .frame(width: 18, height: 18)
                    .scaleEffect(dragging ? 1.15 : 1)
                    .offset(y: y - 9)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { drag in
                    if !dragging { withAnimation(.easeOut(duration: 0.12)) { dragging = true } }
                    let fraction = 1 - min(max(drag.location.y / height, 0), 1)
                    let raw = range.lowerBound + Float(fraction) * (range.upperBound - range.lowerBound)
                    let snapped = (raw * 2).rounded() / 2
                    if snapped != value { value = snapped }
                }
                .onEnded { _ in withAnimation(.easeOut(duration: 0.2)) { dragging = false } })
        }
    }
}

struct LevelMeter: View {
    let level: Float

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.secondary.opacity(0.2))
                Capsule().fill(level > 0.95 ? AnyShapeStyle(Color.red) : AnyShapeStyle(Brand.gradient))
                    .frame(width: geo.size.width * CGFloat(min(level, 1)))
            }
        }
        .help(tr("Mức tín hiệu", "Signal level"))
    }
}

/// Small line drawing of a preset's EQ curve.
struct PresetCurve: View {
    let gains: [Float]

    var body: some View {
        Canvas { context, size in
            guard gains.count > 1 else { return }
            var path = Path()
            for (i, gain) in gains.enumerated() {
                let x = size.width * CGFloat(i) / CGFloat(gains.count - 1)
                let y = size.height / 2 - CGFloat(gain / 12) * size.height / 2
                i == 0 ? path.move(to: CGPoint(x: x, y: y)) : path.addLine(to: CGPoint(x: x, y: y))
            }
            context.stroke(path, with: .linearGradient(Gradient(colors: [.cyan, Brand.accent, .purple]),
                                                       startPoint: .zero, endPoint: CGPoint(x: size.width, y: 0)),
                           style: StrokeStyle(lineWidth: 1.8, lineCap: .round, lineJoin: .round))
        }
    }
}

struct PresetChip: View {
    let preset: Preset
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: preset.icon).frame(width: 18)
                Text(preset.displayName).lineLimit(1)
                Spacer(minLength: 0)
            }
            .font(.callout.weight(selected ? .semibold : .regular))
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background {
                RoundedRectangle(cornerRadius: 9)
                    .fill(selected ? AnyShapeStyle(Brand.gradient)
                          : AnyShapeStyle(Color.secondary.opacity(hovering ? 0.18 : 0.09)))
            }
            .foregroundStyle(selected ? Color.white : Color.primary)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
    }
}

struct StatusBadge: View {
    @Environment(AppState.self) private var state

    var body: some View {
        let (text, color, help): (String, Color, String) =
            if !state.isOn {
                (tr("Đang tắt", "Off"), .secondary, tr("NavTune đang tắt", "NavTune is off"))
            } else if state.errorMessage != nil {
                (tr("Có lỗi", "Error"), .orange, state.errorMessage ?? "")
            } else if state.bypass {
                (tr("Bản gốc", "Original"), .blue, tr("Đang nghe âm thanh gốc để so sánh", "Playing the original sound for comparison"))
            } else if state.engineState == .standby {
                (tr("Chờ âm thanh", "Standby"), .teal,
                 tr("Không có gì đang phát nên NavTune tạm nghỉ: không tốn CPU, máy và tai nghe được nghỉ. Tự chạy lại ngay khi có âm thanh.",
                    "Nothing is playing, so NavTune is resting: no CPU use, and your Mac and headphones can sleep. It resumes instantly when sound plays."))
            } else if state.isProcessing {
                (tr("Đang hoạt động", "Active"), .green, tr("NavTune đang xử lý âm thanh", "NavTune is processing audio"))
            } else {
                (tr("Đang khởi động", "Starting"), .secondary, "")
            }

        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.caption.weight(.medium))
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 4)
        .background(Capsule().fill(color.opacity(0.13)))
        .help(help)
    }
}

/// Error / hint shown at the top of a page.
struct StatusBanner: View {
    @Environment(AppState.self) private var state

    var body: some View {
        if let error = state.errorMessage {
            banner(icon: "exclamationmark.triangle.fill", color: .orange, text: error)
        } else if state.noSignal {
            banner(icon: "ear.trianglebadge.exclamationmark", color: .yellow,
                   text: tr("Ứng dụng đang phát nhưng NavTune không nhận được âm thanh. Hãy kiểm tra quyền ghi âm thanh hệ thống.",
                            "An app is playing but NavTune receives no audio. Check the system audio recording permission."),
                   action: (tr("Mở Cài đặt", "Open Settings"), state.openPrivacySettings))
        }
    }

    private func banner(icon: String, color: Color, text: String, action: (String, () -> Void)? = nil) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon).foregroundStyle(color)
            Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
            Spacer()
            if let action {
                Button(action.0, action: action.1)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(color.opacity(0.12)))
    }
}

/// Frequency (log) / gain (dB) mapping shared by the graph and the parametric editor.
struct GraphScale {
    static let minFrequency = 20.0
    static let maxFrequency = 20000.0
    static let rangeDB = 15.0

    let size: CGSize

    private var span: Double { log10(Self.maxFrequency / Self.minFrequency) }

    func x(_ frequency: Double) -> CGFloat {
        size.width * CGFloat(log10(frequency / Self.minFrequency) / span)
    }

    func frequency(atX x: CGFloat) -> Double {
        Self.minFrequency * pow(10, Double(x / max(size.width, 1)) * span)
    }

    func y(_ db: Double) -> CGFloat {
        size.height / 2 - CGFloat(db / Self.rangeDB) * size.height / 2 * 0.9
    }

    func db(atY y: CGFloat) -> Double {
        Double((size.height / 2 - y) / (size.height / 2 * 0.9)) * Self.rangeDB
    }
}
