// Draws the NavTune app icon (1024x1024 PNG).
// Usage: swift scripts/make_icon.swift <output.png>
import AppKit

let size = 1024
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// macOS icon grid: 824pt body centered in 1024 canvas.
let body = NSRect(x: 100, y: 100, width: 824, height: 824)
let shape = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)

let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
shadow.shadowOffset = NSSize(width: 0, height: -12)
shadow.shadowBlurRadius = 24
NSGraphicsContext.saveGraphicsState()
shadow.set()
NSColor.black.setFill()
shape.fill()
NSGraphicsContext.restoreGraphicsState()

NSGradient(colors: [
    NSColor(red: 0.02, green: 0.62, blue: 0.98, alpha: 1),
    NSColor(red: 0.36, green: 0.27, blue: 0.96, alpha: 1),
    NSColor(red: 0.62, green: 0.16, blue: 0.86, alpha: 1),
])!.draw(in: shape, angle: -55)

// Five EQ sliders.
let knobs: [CGFloat] = [0.38, 0.68, 0.50, 0.80, 0.44]
let bottom = body.minY + body.height * 0.2
let top = body.minY + body.height * 0.8
for (i, knob) in knobs.enumerated() {
    let x = body.minX + body.width * (0.22 + 0.14 * CGFloat(i))
    let rail = NSBezierPath()
    rail.move(to: NSPoint(x: x, y: bottom))
    rail.line(to: NSPoint(x: x, y: top))
    rail.lineWidth = 30
    rail.lineCapStyle = .round
    NSColor.white.withAlphaComponent(0.28).setStroke()
    rail.stroke()

    let y = bottom + (top - bottom) * knob
    let fill = NSBezierPath()
    fill.move(to: NSPoint(x: x, y: bottom))
    fill.line(to: NSPoint(x: x, y: y))
    fill.lineWidth = 30
    fill.lineCapStyle = .round
    NSColor.white.withAlphaComponent(0.9).setStroke()
    fill.stroke()

    NSColor.white.setFill()
    NSBezierPath(ovalIn: NSRect(x: x - 50, y: y - 50, width: 100, height: 100)).fill()
}

NSGraphicsContext.restoreGraphicsState()
let output = URL(fileURLWithPath: CommandLine.arguments[1])
try! rep.representation(using: .png, properties: [:])!.write(to: output)
