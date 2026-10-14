import AppKit

/// Masks hold coverage per pixel (0–255), row 0 at the top, at `scale`× the menu bar point size.
enum LogoGauge {
    static let scale = 2

    static let unavailable = NSImage(systemSymbolName: "exclamationmark.triangle", accessibilityDescription: nil)!

    struct Part {
        let solid: [UInt8]
        let top: Int
        let bottom: Int

        init?(solid: [UInt8], width: Int) {
            let height = solid.count / width
            let inkRows = (0..<height).filter { row in
                (0..<width).contains { solid[row * width + $0] > 32 }
            }
            guard let top = inkRows.first, let bottom = inkRows.last else { return nil }
            (self.solid, self.top, self.bottom) = (solid, top, bottom)
        }
    }

    /// A nil percentage (unknown) shows that part as a faint solid shape.
    static func image(_ parts: [(Part, Double?)], width: Int, height: Int) -> NSImage {
        var alpha = [UInt8](repeating: 0, count: width * height)
        for (part, remainingPercent) in parts {
            guard let remaining = remainingPercent else {
                for i in alpha.indices { alpha[i] = max(alpha[i], UInt8(Double(part.solid[i]) * 0.4)) }
                continue
            }
            let fraction = max(0, min(1, remaining / 100))
            let span = Double(part.bottom + 1 - part.top)
            let fillTop = Double(part.bottom + 1) - fraction * span
            for row in 0..<height {
                let filled = max(0, min(1, Double(row + 1) - fillTop))
                for col in 0..<width {
                    let i = row * width + col
                    let value = (0.3 + 0.7 * filled) * Double(part.solid[i])
                    alpha[i] = max(alpha[i], UInt8(value))
                }
            }
        }

        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
            bitsPerSample: 8, samplesPerPixel: 2, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceWhite, bytesPerRow: width * 2, bitsPerPixel: 16
        )!
        let data = rep.bitmapData!
        for (i, a) in alpha.enumerated() {
            data[i * 2] = 0 // only alpha matters for template images
            data[i * 2 + 1] = a
        }
        let size = NSSize(width: width / scale, height: height / scale)
        rep.size = size
        let image = NSImage(size: size)
        image.addRepresentation(rep)
        image.isTemplate = true
        return image
    }

    /// Ink coverage of a black logo; `source` is in image points, `.zero` for the whole image.
    static func rasterize(_ logo: NSImage, from source: NSRect = .zero, into dest: NSRect,
                          width: Int, height: Int) -> [UInt8] {
        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
            bitsPerSample: 8, samplesPerPixel: 1, hasAlpha: false, isPlanar: false,
            colorSpaceName: .deviceWhite, bytesPerRow: width, bitsPerPixel: 8
        )!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSGraphicsContext.current?.imageInterpolation = .high
        NSColor.white.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        logo.draw(in: dest, from: source, operation: .sourceOver, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()

        let gray = rep.bitmapData!
        return (0..<width * height).map { 255 - gray[$0] }
    }

    static func logo(named name: String) -> NSImage? {
        if let url = Bundle.main.url(forResource: name, withExtension: "png") { return NSImage(contentsOf: url) }
        // `swift run` has no app bundle; use the copy in the source tree.
        let dev = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/\(name).png")
        return NSImage(contentsOf: dev)
    }
}
