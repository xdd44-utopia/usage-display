import AppKit

enum ClaudeIcon {
    private static let height = 18 * LogoGauge.scale
    private static let glyphHeight = 14 * LogoGauge.scale // the mark is wide; full height looks heavy

    private static let mark: (a: LogoGauge.Part, slash: LogoGauge.Part, width: Int)? = loadMark()

    static func image(fiveHourPercent: Double?, weeklyPercent: Double?) -> NSImage {
        guard let mark else { return LogoGauge.unavailable }
        return LogoGauge.image([(mark.a, fiveHourPercent), (mark.slash, weeklyPercent)],
                               width: mark.width, height: height)
    }

    private static func loadMark() -> (a: LogoGauge.Part, slash: LogoGauge.Part, width: Int)? {
        guard let logo = LogoGauge.logo(named: "anthropic-logo") else { return nil }

        let w = Int(logo.size.width), h = Int(logo.size.height)
        let full = LogoGauge.rasterize(logo, into: NSRect(x: 0, y: 0, width: w, height: h), width: w, height: h)
        let inkCols = (0..<w).filter { col in (0..<h).contains { full[$0 * w + col] > 127 } }
        let inkRows = (0..<h).filter { row in (0..<w).contains { full[row * w + $0] > 127 } }
        guard let left = inkCols.first, let right = inkCols.last,
              let top = inkRows.first, let bottom = inkRows.last else { return nil }
        let source = NSRect(x: left, y: h - 1 - bottom, width: right + 1 - left, height: bottom + 1 - top)

        let glyphWidth = Int((Double(glyphHeight) * source.width / source.height).rounded())
        let width = (glyphWidth + 3) / 2 * 2
        let dest = NSRect(x: (width - glyphWidth) / 2, y: (height - glyphHeight) / 2,
                          width: glyphWidth, height: glyphHeight)
        let solid = LogoGauge.rasterize(logo, from: source, into: dest, width: width, height: height)

        guard let labels = splitShapes(solid, width: width, count: 2) else { return nil }
        func part(_ label: Int) -> LogoGauge.Part? {
            LogoGauge.Part(solid: solid.indices.map { labels[$0] == label ? solid[$0] : 0 },
                           width: width)
        }
        guard let a = part(0), let slash = part(1) else { return nil }
        return (a, slash, width)
    }

    /// Labels each inked pixel by connected shape, leftmost 0; nil unless there are exactly `count`.
    private static func splitShapes(_ mask: [UInt8], width: Int, count: Int) -> [Int]? {
        let height = mask.count / width
        func neighbors(_ i: Int) -> [Int] {
            let row = i / width, col = i % width
            var result: [Int] = []
            if row > 0 { result.append(i - width) }
            if row < height - 1 { result.append(i + width) }
            if col > 0 { result.append(i - 1) }
            if col < width - 1 { result.append(i + 1) }
            return result
        }

        var labels = [Int](repeating: -1, count: mask.count)
        var leftmost: [Int] = []
        for start in mask.indices where mask[start] >= 128 && labels[start] < 0 {
            let label = leftmost.count
            var minCol = width
            var stack = [start]
            labels[start] = label
            while let i = stack.popLast() {
                minCol = min(minCol, i % width)
                for n in neighbors(i) where mask[n] >= 128 && labels[n] < 0 {
                    labels[n] = label
                    stack.append(n)
                }
            }
            leftmost.append(minCol)
        }
        guard leftmost.count == count else { return nil }

        var queue = labels.indices.filter { labels[$0] >= 0 }
        var head = 0
        while head < queue.count {
            let i = queue[head]
            head += 1
            for n in neighbors(i) where mask[n] > 0 && labels[n] < 0 {
                labels[n] = labels[i]
                queue.append(n)
            }
        }

        let order = leftmost.indices.sorted { leftmost[$0] < leftmost[$1] }
        var rank = [Int](repeating: 0, count: count)
        for (position, label) in order.enumerated() { rank[label] = position }
        return labels.map { $0 >= 0 ? rank[$0] : -1 }
    }
}
