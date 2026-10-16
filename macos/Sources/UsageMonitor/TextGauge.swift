import AppKit

enum TextGauge {
    static let height: CGFloat = 18
    private static let letterGap: CGFloat = 1.5
    private static let labelGap: CGFloat = 5
    private static let rowGap: CGFloat = 2.5

    static func claude(fiveHourPercent: Double?, weeklyPercent: Double?) -> NSImage {
        image(label: "CLA", rows: [(weeklyPercent, 7.5), (fiveHourPercent, 12)])
    }

    static func codex(remainingPercent: Double?) -> NSImage {
        image(label: "COD", rows: [(remainingPercent, 15)])
    }

    /// Rows are (percent, font size), top to bottom.
    private static func image(label: String, rows: [(Double?, CGFloat)]) -> NSImage {
        let numbers = rows.map { percent, size in
            text(percent.map { "\(Int($0.rounded()))%" } ?? "--%",
                 font: .monospacedDigitSystemFont(ofSize: size, weight: .regular),
                 alpha: percent == nil ? 0.4 : 1)
        }
        let inks = numbers.map { $0.boundingRect(with: .zero, options: .usesDeviceMetrics) }
        let capHeights = rows.map { NSFont.monospacedDigitSystemFont(ofSize: $0.1, weight: .regular).capHeight }
        let stack = capHeights.reduce(0, +) + rowGap * CGFloat(rows.count - 1)
        let letters = stacked(label)
        let labelWidth = letters.map(\.ink.width).max() ?? 0
        let numbersX = labelWidth + labelGap
        let width = ceil(numbersX + (inks.map { $0.maxX - $0.minX }.max() ?? 0))

        let image = NSImage(size: NSSize(width: width, height: height), flipped: false) { _ in
            for (letter, ink, baseline) in letters {
                letter.draw(with: NSRect(x: (labelWidth - ink.width) / 2 - ink.minX, y: baseline,
                                         width: 0, height: 0), options: [])
            }

            var baseline = (height + stack) / 2
            for (number, (ink, capHeight)) in zip(numbers, zip(inks, capHeights)) {
                baseline -= capHeight
                number.draw(with: NSRect(x: numbersX - ink.minX, y: baseline, width: 0, height: 0), options: [])
                baseline -= rowGap
            }
            return true
        }
        image.isTemplate = true
        return image
    }

    /// Capital letters in the system monospaced font, top to bottom, with their caps filling `height`.
    private static func stacked(_ label: String) -> [(letter: NSAttributedString, ink: NSRect, baseline: CGFloat)] {
        let count = CGFloat(label.count)
        let capHeight = (height - letterGap * (count - 1)) / count
        let reference = NSFont.monospacedSystemFont(ofSize: 100, weight: .regular).capHeight
        let font = NSFont.monospacedSystemFont(ofSize: 100 * capHeight / reference, weight: .regular)
        return label.enumerated().map { index, character in
            let letter = text(String(character), font: font)
            let baseline = height - CGFloat(index + 1) * capHeight - CGFloat(index) * letterGap
            return (letter, letter.boundingRect(with: .zero, options: .usesDeviceMetrics), baseline)
        }
    }

    private static func text(_ string: String, font: NSFont, alpha: CGFloat = 1) -> NSAttributedString {
        NSAttributedString(string: string, attributes: [
            .font: font, .foregroundColor: NSColor.black.withAlphaComponent(alpha), .kern: 0
        ])
    }
}
