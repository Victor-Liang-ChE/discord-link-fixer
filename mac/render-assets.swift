import AppKit

let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

func png(width: Int, height: Int, destination: URL, draw: () -> Void) throws {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                              bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                              isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw()
    NSGraphicsContext.restoreGraphicsState()
    try rep.representation(using: .png, properties: [:])!.write(to: destination)
}

let iconset = root.appendingPathComponent("AppIcon.iconset", isDirectory: true)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = size * scale
        let suffix = scale == 2 ? "@2x" : ""
        try png(width: pixels, height: pixels, destination: iconset.appendingPathComponent("icon_\(size)x\(size)\(suffix).png")) {
            let side = CGFloat(pixels)
            NSColor(calibratedRed: 0.13, green: 0.37, blue: 0.78, alpha: 1).setFill()
            NSBezierPath(roundedRect: NSRect(x: side * 0.08, y: side * 0.08, width: side * 0.84, height: side * 0.84),
                         xRadius: side * 0.19, yRadius: side * 0.19).fill()
            let symbol = NSImage(systemSymbolName: "link", accessibilityDescription: nil)!
                .withSymbolConfiguration(NSImage.SymbolConfiguration(paletteColors: [.white]))!
            symbol.draw(in: NSRect(x: side * 0.23, y: side * 0.23, width: side * 0.54, height: side * 0.54))
        }
    }
}
try png(width: 600, height: 360, destination: root.appendingPathComponent("install.png")) {
    NSColor(calibratedWhite: 0.97, alpha: 1).setFill()
    NSRect(x: 0, y: 0, width: 600, height: 360).fill()
    func text(_ value: String, y: CGFloat, size: CGFloat, weight: NSFont.Weight) {
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: weight),
                                                       .foregroundColor: NSColor(calibratedWhite: 0.2, alpha: 1)]
        let width = (value as NSString).size(withAttributes: attributes).width
        (value as NSString).draw(at: NSPoint(x: (600 - width) / 2, y: y), withAttributes: attributes)
    }
    text("Discord Link Fixer", y: 307, size: 24, weight: .semibold)
    text("Drag the app into Applications", y: 280, size: 15, weight: .regular)
    NSColor(calibratedWhite: 0.5, alpha: 1).setStroke()
    let arrow = NSBezierPath()
    arrow.lineWidth = 7
    arrow.lineCapStyle = .round
    arrow.lineJoinStyle = .round
    arrow.move(to: NSPoint(x: 257, y: 183))
    arrow.line(to: NSPoint(x: 343, y: 183))
    arrow.move(to: NSPoint(x: 323, y: 203))
    arrow.line(to: NSPoint(x: 343, y: 183))
    arrow.line(to: NSPoint(x: 323, y: 163))
    arrow.stroke()
    text("Then open the installed app to finish setup", y: 57, size: 13, weight: .regular)
}
