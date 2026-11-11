// Draws the BSSIDLock app icon (Wi-Fi arcs radiating from a padlock) as a 1024x1024 PNG.
// Usage: swift assets/make-icon.swift assets/icon-1024.png
import AppKit

let out = CommandLine.arguments.dropFirst().first ?? "icon-1024.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024,
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// Background: rounded square on the standard macOS icon grid (824pt body on a 1024 canvas).
let body = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 185, yRadius: 185)
NSGraphicsContext.saveGraphicsState()
let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.28)
shadow.shadowBlurRadius = 28
shadow.shadowOffset = NSSize(width: 0, height: -12)
shadow.set()
NSColor.black.setFill()
body.fill()
NSGraphicsContext.restoreGraphicsState()
let top = NSColor(srgbRed: 0.16, green: 0.56, blue: 1.00, alpha: 1)
let bottom = NSColor(srgbRed: 0.30, green: 0.22, blue: 0.82, alpha: 1)
NSGradient(starting: top, ending: bottom)!.draw(in: body, angle: -90)

NSColor.white.setStroke()
NSColor.white.setFill()

// Wi-Fi arcs, centered on the padlock.
let center = NSPoint(x: 512, y: 400)
for r in [170.0, 270.0, 370.0] {
    let arc = NSBezierPath()
    arc.appendArc(withCenter: center, radius: r, startAngle: 45, endAngle: 135, clockwise: false)
    arc.lineWidth = 60
    arc.lineCapStyle = .round
    arc.stroke()
}

// Padlock shackle.
let shackle = NSBezierPath()
shackle.move(to: NSPoint(x: 562, y: 385))
shackle.line(to: NSPoint(x: 562, y: 410))
shackle.appendArc(withCenter: NSPoint(x: 512, y: 410), radius: 50, startAngle: 0, endAngle: 180, clockwise: false)
shackle.line(to: NSPoint(x: 462, y: 385))
shackle.lineWidth = 28
shackle.stroke()

// Padlock body.
NSBezierPath(roundedRect: NSRect(x: 422, y: 250, width: 180, height: 140), xRadius: 28, yRadius: 28).fill()

// Keyhole.
let hole = NSColor(srgbRed: 0.24, green: 0.36, blue: 0.90, alpha: 1)
hole.setFill()
NSBezierPath(ovalIn: NSRect(x: 494, y: 318, width: 36, height: 36)).fill()
NSBezierPath(roundedRect: NSRect(x: 503, y: 282, width: 18, height: 46), xRadius: 6, yRadius: 6).fill()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
print("Wrote \(out)")
