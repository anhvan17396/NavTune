import AppKit
import QuartzCore
import SwiftUI

/// Base for live meters drawn with Core Animation. Each frame only hands new geometry to the render server:
/// no SwiftUI updates and no rasterization on the main thread. The display link runs only while
/// audio is processed and the window is actually on screen.
class LiveLayerView: NSView {
    var isActive = false {
        didSet { if isActive != oldValue { updateLink() } }
    }
    var frameRate: Float = 30

    private var link: CADisplayLink?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)
        let center = NotificationCenter.default
        if let window {
            center.removeObserver(self, name: NSWindow.didChangeOcclusionStateNotification, object: window)
        }
        if let newWindow {
            center.addObserver(self, selector: #selector(visibilityChanged),
                               name: NSWindow.didChangeOcclusionStateNotification, object: newWindow)
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        updateLink()
    }

    override func layout() {
        super.layout()
        withoutAnimation { layoutLayers() }
    }

    @objc private func visibilityChanged(_ notification: Notification) {
        updateLink()
    }

    private func updateLink() {
        let onScreen = window.map { $0.isVisible && $0.occlusionState.contains(.visible) } ?? false
        if isActive && onScreen {
            guard link == nil else { return }
            let link = displayLink(target: self, selector: #selector(step))
            link.preferredFrameRateRange = CAFrameRateRange(minimum: frameRate / 2, maximum: frameRate, preferred: frameRate)
            link.add(to: .main, forMode: .common)
            self.link = link
        } else {
            link?.invalidate()
            link = nil
            withoutAnimation { reset() }
        }
    }

    @objc private func step(_ link: CADisplayLink) {
        withoutAnimation { draw(at: Date()) }
    }

    func withoutAnimation(_ body: () -> Void) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        body()
        CATransaction.commit()
    }

    // Overridden by subclasses.
    func layoutLayers() {}
    func draw(at date: Date) {}
    func reset() {}
}

final class SpectrumLayerView: LiveLayerView {
    var bands: (@MainActor (Date) -> [Float])?

    private let gradient = CAGradientLayer()
    private let shape = CAShapeLayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        gradient.colors = [NSColor.systemCyan.withAlphaComponent(0.5).cgColor,
                           NSColor.systemPurple.withAlphaComponent(0.12).cgColor]
        // Layer coordinates start at the bottom: y = 1 is the top edge.
        gradient.startPoint = CGPoint(x: 0.5, y: 1)
        gradient.endPoint = CGPoint(x: 0.5, y: 0)
        gradient.mask = shape
        layer?.addSublayer(gradient)
    }

    override func layoutLayers() {
        gradient.frame = bounds
        shape.frame = bounds
    }

    override func draw(at date: Date) {
        guard let bands = bands?(date), bands.count > 1 else { return }
        let width = bounds.width, height = bounds.height
        let points = bands.enumerated().map { i, level in
            CGPoint(x: width * (CGFloat(i) + 0.5) / CGFloat(bands.count), y: height * CGFloat(level) * 0.92)
        }
        let path = CGMutablePath()
        path.move(to: .zero)
        path.addLine(to: points[0])
        for (a, b) in zip(points, points.dropFirst()) {
            path.addQuadCurve(to: CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2), control: a)
        }
        path.addLine(to: points[points.count - 1])
        path.addLine(to: CGPoint(x: width, y: 0))
        path.closeSubpath()
        shape.path = path
    }

    override func reset() {
        shape.path = nil
    }
}

final class LevelLayerView: LiveLayerView {
    var level: (@MainActor () -> Float)?

    private let fill = CAGradientLayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        frameRate = 20
        layer?.backgroundColor = NSColor.secondaryLabelColor.withAlphaComponent(0.2).cgColor
        layer?.masksToBounds = true
        fill.colors = [NSColor(red: 0.02, green: 0.62, blue: 0.98, alpha: 1).cgColor,
                       NSColor(red: 0.62, green: 0.16, blue: 0.86, alpha: 1).cgColor]
        fill.startPoint = CGPoint(x: 0, y: 0.5)
        fill.endPoint = CGPoint(x: 1, y: 0.5)
        layer?.addSublayer(fill)
    }

    override func layoutLayers() {
        layer?.cornerRadius = bounds.height / 2
        fill.frame.size.height = bounds.height
    }

    override func draw(at date: Date) {
        let value = CGFloat(min(level?() ?? 0, 1))
        fill.frame = CGRect(x: 0, y: 0, width: bounds.width * value, height: bounds.height)
        fill.cornerRadius = bounds.height / 2
        fill.opacity = value > 0.95 ? 0.6 : 1
    }

    override func reset() {
        fill.frame.size.width = 0
    }
}

struct LiveSpectrum: View {
    @Environment(AppState.self) private var state

    var body: some View {
        LiveSpectrumRepresentable(state: state, isActive: state.isProcessing)
    }
}

private struct LiveSpectrumRepresentable: NSViewRepresentable {
    let state: AppState
    let isActive: Bool

    func makeNSView(context: Context) -> SpectrumLayerView {
        let view = SpectrumLayerView()
        view.bands = { [state] date in state.spectrumBands(at: date) }
        return view
    }

    func updateNSView(_ view: SpectrumLayerView, context: Context) {
        view.isActive = isActive
    }
}

/// A level meter that animates only while audio is processed and its window is on screen.
struct LiveLevelMeter: View {
    @Environment(AppState.self) private var state

    var body: some View {
        LiveLevelRepresentable(state: state, isActive: state.isProcessing)
            .help(tr("Mức tín hiệu", "Signal level"))
    }
}

private struct LiveLevelRepresentable: NSViewRepresentable {
    let state: AppState
    let isActive: Bool

    func makeNSView(context: Context) -> LevelLayerView {
        let view = LevelLayerView()
        view.level = { [state] in state.outputLevel }
        return view
    }

    func updateNSView(_ view: LevelLayerView, context: Context) {
        view.isActive = isActive
    }
}
