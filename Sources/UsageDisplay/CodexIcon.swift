import AppKit

enum CodexIcon {
    private static let pixels = 18 * LogoGauge.scale

    private static let part: LogoGauge.Part? = loadPart()

    static func image(remainingPercent: Double?) -> NSImage {
        guard let part else { return LogoGauge.unavailable }
        return LogoGauge.image([(part, remainingPercent)], width: pixels, height: pixels)
    }

    private static func loadPart() -> LogoGauge.Part? {
        guard let logo = LogoGauge.logo(named: "codex-logo") else { return nil }
        let solid = LogoGauge.rasterize(
            logo, into: NSRect(x: 0, y: 0, width: pixels, height: pixels), width: pixels, height: pixels)

        return LogoGauge.Part(solid: solid, width: pixels)
    }
}
