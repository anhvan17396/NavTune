import SwiftUI

/// Live spectrum of the output with the EQ response curve drawn on top.
/// Three independent layers keep redraws cheap: the grid draws once, the curve only when settings change,
/// and the spectrum animates only while audio is processed and the window is on screen.
struct FrequencyGraph: View {
    let settings: SoundSettings
    let sampleRate: Double
    var showsGrid = true
    var cornerRadius: CGFloat = 10

    var body: some View {
        ZStack {
            if showsGrid { GraphGrid() }
            LiveSpectrum()
            ResponseCurve(settings: settings, sampleRate: sampleRate > 0 ? sampleRate : 48000)
        }
        .background(Brand.graphBackground)
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
    }
}

private struct GraphGrid: View {
    var body: some View {
        Canvas { context, size in
            let scale = GraphScale(size: size)
            let line = Color.white.opacity(0.07)
            for f in [50.0, 100, 200, 500, 1000, 2000, 5000, 10000] {
                let px = scale.x(f)
                context.stroke(Path { $0.move(to: CGPoint(x: px, y: 0)); $0.addLine(to: CGPoint(x: px, y: size.height)) },
                               with: .color(line))
                context.draw(Text(Format.frequency(f)).font(.system(size: 9, weight: .medium)).foregroundStyle(.white.opacity(0.45)),
                             at: CGPoint(x: px, y: size.height - 9))
            }
            for db in [-12.0, -6, 0, 6, 12] {
                let py = scale.y(db)
                context.stroke(Path { $0.move(to: CGPoint(x: 0, y: py)); $0.addLine(to: CGPoint(x: size.width, y: py)) },
                               with: .color(db == 0 ? Color.white.opacity(0.18) : line))
                context.draw(Text(db == 0 ? "0" : String(format: "%+.0f", db)).font(.system(size: 9, weight: .medium))
                                .foregroundStyle(.white.opacity(0.45)),
                             at: CGPoint(x: 14, y: py - 7))
            }
        }
    }
}

@MainActor
private enum ResponseTables {
    private static var cache: [Double: ResponseTable] = [:]

    static func table(for sampleRate: Double) -> ResponseTable {
        if let table = cache[sampleRate] { return table }
        let table = ResponseTable(sampleRate: sampleRate, count: 160,
                                  from: GraphScale.minFrequency, to: GraphScale.maxFrequency)
        cache[sampleRate] = table
        return table
    }
}

private struct ResponseCurve: View {
    let settings: SoundSettings
    let sampleRate: Double

    var body: some View {
        let table = ResponseTables.table(for: sampleRate)
        let response = table.decibels(for: AudioProcessor.filters(for: settings, sampleRate: sampleRate),
                                      gain: Double(settings.preamp))
        Canvas { context, size in
            let scale = GraphScale(size: size)
            var curve = Path()
            for (i, db) in response.enumerated() {
                let point = CGPoint(x: scale.x(table.frequencies[i]),
                                    y: scale.y(min(max(db, -GraphScale.rangeDB), GraphScale.rangeDB)))
                i == 0 ? curve.move(to: point) : curve.addLine(to: point)
            }

            var area = curve
            area.addLine(to: CGPoint(x: size.width, y: scale.y(0)))
            area.addLine(to: CGPoint(x: 0, y: scale.y(0)))
            area.closeSubpath()
            context.fill(area, with: .color(Brand.accent.opacity(0.18)))

            // A wide translucent stroke under the line gives the glow without an expensive blur.
            context.stroke(curve, with: .color(Brand.accent.opacity(0.45)), style: StrokeStyle(lineWidth: 6, lineCap: .round, lineJoin: .round))
            context.stroke(curve, with: .color(.white), style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
        }
    }
}
