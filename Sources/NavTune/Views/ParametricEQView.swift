import SwiftUI

/// Parametric EQ: drag the numbered points on the graph, fine-tune the selected band below.
struct ParametricEQView: View {
    @Environment(AppState.self) private var state
    @State private var selection: EQBand.ID?

    private var bands: [EQBand] { state.settings.bands }
    private var selected: EQBand? { bands.first { $0.id == selection } ?? bands.first }

    var body: some View {
        Card(title: tr("EQ tham số", "Parametric EQ"), icon: "dial.medium",
             trailing: AnyView(ValuePill(text: state.currentPresetName))) {
            graph.frame(height: 260)
            bandStrip
            if let band = selected { inspector(band) }
        }
    }

    private var graph: some View {
        GeometryReader { geo in
            let scale = GraphScale(size: geo.size)
            ZStack {
                FrequencyGraph(settings: state.settings, sampleRate: state.sampleRate)
                ForEach(Array(bands.enumerated()), id: \.element.id) { index, band in
                    BandNode(number: index + 1, band: band, selected: band.id == selected?.id)
                        .position(x: scale.x(band.frequency),
                                  y: scale.y(band.kind.hasGain ? Double(band.gain) : 0))
                        .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("graph"))
                            .onChanged { drag in
                                selection = band.id
                                let frequency = min(max(scale.frequency(atX: drag.location.x), 20), 20000)
                                let gain = Float(min(max(scale.db(atY: drag.location.y), -12), 12))
                                state.updateBand(id: band.id) {
                                    $0.frequency = frequency.rounded()
                                    if $0.kind.hasGain { $0.gain = (gain * 2).rounded() / 2 }
                                }
                            })
                }
            }
            .coordinateSpace(.named("graph"))
        }
    }

    private var bandStrip: some View {
        HStack(spacing: 8) {
            ForEach(Array(bands.enumerated()), id: \.element.id) { index, band in
                Button {
                    selection = band.id
                } label: {
                    VStack(spacing: 2) {
                        Text("\(index + 1)").font(.caption.bold())
                        Text(Format.frequency(band.frequency)).font(.caption2.monospacedDigit())
                    }
                    .frame(width: 44, height: 36)
                    .background(RoundedRectangle(cornerRadius: 8)
                        .fill(band.id == selected?.id ? AnyShapeStyle(Brand.gradient) : AnyShapeStyle(Color.secondary.opacity(0.1))))
                    .foregroundStyle(band.id == selected?.id ? Color.white : band.enabled ? Color.primary : Color.secondary)
                }
                .buttonStyle(.plain)
            }
            Button {
                if let id = state.addBand() { selection = id }
            } label: {
                Label(tr("Thêm băng", "Add band"), systemImage: "plus")
            }
            .disabled(bands.count >= SoundSettings.maxParametricBands)
            Spacer()
            Text(tr("Kéo các điểm trên biểu đồ để chỉnh", "Drag the points on the graph"))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func inspector(_ band: EQBand) -> some View {
        let id = band.id
        return VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Picker(tr("Kiểu lọc", "Filter type"), selection: Binding(
                    get: { band.kind },
                    set: { kind in state.updateBand(id: id) { $0.kind = kind } })) {
                    ForEach(FilterKind.allCases) { Text($0.title).tag($0) }
                }
                .frame(maxWidth: 280)
                Toggle(tr("Bật", "On"), isOn: Binding(
                    get: { band.enabled },
                    set: { on in state.updateBand(id: id) { $0.enabled = on } }))
                Spacer()
                Button(role: .destructive) {
                    state.removeBand(id: id)
                    selection = nil
                } label: {
                    Label(tr("Xoá băng", "Remove band"), systemImage: "trash")
                }
                .disabled(bands.count <= 1)
            }

            HStack(spacing: 24) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(tr("Tần số", "Frequency"))
                        Spacer()
                        ValuePill(text: Format.hertz(band.frequency))
                    }
                    Slider(value: Binding(
                        get: { log10(band.frequency / 20) / 3 },
                        set: { t in state.updateBand(id: id) { $0.frequency = (20 * pow(1000, t)).rounded() } }))
                }
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(tr("Mức", "Gain"))
                        Spacer()
                        ValuePill(text: band.kind.hasGain ? Format.dB(band.gain) : "—")
                    }
                    Slider(value: Binding(
                        get: { Double(band.gain) },
                        set: { g in state.updateBand(id: id) { $0.gain = (Float(g) * 2).rounded() / 2 } }),
                           in: -12...12)
                        .disabled(!band.kind.hasGain)
                }
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(tr("Độ rộng (Q)", "Width (Q)"))
                        Spacer()
                        ValuePill(text: String(format: "%.2f", band.q))
                    }
                    Slider(value: Binding(
                        get: { log10(band.q / 0.1) / 2 },
                        set: { t in state.updateBand(id: id) { $0.q = (0.1 * pow(100, t) * 100).rounded() / 100 } }))
                }
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.secondary.opacity(0.07)))
    }
}

private struct BandNode: View {
    let number: Int
    let band: EQBand
    let selected: Bool
    @State private var hovering = false

    var body: some View {
        Text("\(number)")
            .font(.system(size: 11, weight: .bold))
            .foregroundStyle(selected ? Color.white : Brand.accent)
            .frame(width: 24, height: 24)
            .background(Circle().fill(selected ? AnyShapeStyle(Brand.gradient) : AnyShapeStyle(Color.white)))
            .overlay(Circle().strokeBorder(.white.opacity(selected ? 0.9 : 0), lineWidth: 2))
            .shadow(color: Brand.accent.opacity(selected || hovering ? 0.8 : 0.3), radius: selected ? 8 : 4)
            .opacity(band.enabled ? 1 : 0.45)
            .scaleEffect(hovering ? 1.12 : 1)
            .onHover { hovering = $0 }
            .animation(.easeOut(duration: 0.12), value: hovering)
            .help("\(band.kind.title) · \(Format.hertz(band.frequency))")
    }
}
