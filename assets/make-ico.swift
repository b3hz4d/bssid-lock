// Builds a Windows .ico (16–256 px) from the 1024px PNG icon.
// Small sizes are stored as 32-bit BMP (what every Windows API expects), 256 px as PNG.
// Usage: swift assets/make-ico.swift assets/icon-1024.png windows/BSSIDLock/BSSIDLock.ico
import AppKit

let args = CommandLine.arguments
guard args.count == 3, let src = NSImage(contentsOfFile: args[1]) else {
    print("usage: make-ico.swift <icon-1024.png> <out.ico>"); exit(1)
}

func render(_ size: Int) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32)!
    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    ctx.imageInterpolation = .high
    NSGraphicsContext.current = ctx
    // The source has ~10% transparent margin (macOS grid); crop most of it so small Windows icons stay legible.
    let inset = 1024.0 * 0.08
    src.draw(in: NSRect(x: 0, y: 0, width: size, height: size),
             from: NSRect(x: inset, y: inset, width: 1024 - 2 * inset, height: 1024 - 2 * inset),
             operation: .copy, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xff), UInt8((v >> 8) & 0xff)] }
func le32(_ v: Int) -> [UInt8] { le16(v & 0xffff) + le16((v >> 16) & 0xffff) }

/// 32-bit BGRA DIB with an all-zero AND mask (alpha does the masking).
func dib(_ rep: NSBitmapImageRep) -> [UInt8] {
    let w = rep.pixelsWide, h = rep.pixelsHigh
    let maskRow = ((w + 31) / 32) * 4
    var out = le32(40) + le32(w) + le32(h * 2) + le16(1) + le16(32) + le32(0)
        + le32(w * h * 4 + maskRow * h) + le32(0) + le32(0) + le32(0) + le32(0)
    let p = rep.bitmapData!
    for y in stride(from: h - 1, through: 0, by: -1) {          // bottom-up
        for x in 0..<w {
            let i = y * rep.bytesPerRow + x * 4
            let r = Int(p[i]), g = Int(p[i + 1]), b = Int(p[i + 2]), a = Int(p[i + 3])
            func un(_ c: Int) -> UInt8 { a == 0 ? 0 : UInt8(min(255, c * 255 / a)) }  // un-premultiply
            out += [un(b), un(g), un(r), UInt8(a)]
        }
    }
    out += [UInt8](repeating: 0, count: maskRow * h)
    return out
}

let sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
var images: [[UInt8]] = []
for s in sizes {
    let rep = render(s)
    images.append(s >= 256 ? [UInt8](rep.representation(using: .png, properties: [:])!) : dib(rep))
}

var ico = le16(0) + le16(1) + le16(sizes.count)
var offset = 6 + 16 * sizes.count
for (s, img) in zip(sizes, images) {
    ico += [UInt8(s >= 256 ? 0 : s), UInt8(s >= 256 ? 0 : s), 0, 0] + le16(1) + le16(32) + le32(img.count) + le32(offset)
    offset += img.count
}
for img in images { ico += img }
try! Data(ico).write(to: URL(fileURLWithPath: args[2]))
print("Wrote \(args[2]) (\(sizes.map(String.init).joined(separator: ", ")) px)")
