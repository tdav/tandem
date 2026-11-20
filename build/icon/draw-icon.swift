import AppKit

// Draws the Tandem app icon (1024×1024): a macOS-style rounded square with a deep blue → cyan gradient,
// two light "panels" side by side, and an amber arc arrow carrying a file from the left panel to the right one.
let size: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon_1024.png"

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

func color(_ hex: UInt32, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

// Apple icon grid: 824×824 body centred on the 1024 canvas, corner radius ≈ 185.
let body = NSRect(x: 100, y: 100, width: 824, height: 824)
let bodyPath = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)

// Soft drop shadow under the body.
ctx.saveGState()
let shadow = NSShadow()
shadow.shadowColor = color(0x000000, 0.35)
shadow.shadowOffset = NSSize(width: 0, height: -14)
shadow.shadowBlurRadius = 28
shadow.set()
color(0x1E3A8A).setFill()
bodyPath.fill()
ctx.restoreGState()

// Body gradient.
ctx.saveGState()
bodyPath.addClip()
NSGradient(colors: [color(0x1E2A78), color(0x2563EB), color(0x06B6D4)],
           atLocations: [0, 0.55, 1], colorSpace: .sRGB)!.draw(in: body, angle: -55)
// Gentle top highlight.
NSGradient(colors: [color(0xFFFFFF, 0.18), color(0xFFFFFF, 0)])!
    .draw(in: NSRect(x: body.minX, y: body.midY, width: body.width, height: body.height / 2), angle: -90)
ctx.restoreGState()

// Two panels.
func panel(_ rect: NSRect, highlightRow: Int?, alpha: CGFloat) {
    ctx.saveGState()
    let s = NSShadow()
    s.shadowColor = color(0x0B1340, 0.35)
    s.shadowOffset = NSSize(width: 0, height: -8)
    s.shadowBlurRadius = 18
    s.set()
    color(0xFFFFFF, alpha).setFill()
    NSBezierPath(roundedRect: rect, xRadius: 42, yRadius: 42).fill()
    ctx.restoreGState()

    // Title strip.
    let strip = NSRect(x: rect.minX + 26, y: rect.maxY - 70, width: rect.width - 52, height: 26)
    color(0x2563EB, 0.85).setFill()
    NSBezierPath(roundedRect: strip, xRadius: 13, yRadius: 13).fill()

    // File rows.
    for i in 0..<5 {
        let y = rect.maxY - 130 - CGFloat(i) * 62
        let row = NSRect(x: rect.minX + 26, y: y, width: rect.width - 52, height: 34)
        if i == highlightRow {
            color(0xF59E0B).setFill()
            NSBezierPath(roundedRect: row.insetBy(dx: -8, dy: -8), xRadius: 16, yRadius: 16).fill()
            color(0xFFFFFF, 0.95).setFill()
        } else {
            color(0x94A3B8, 0.55).setFill()
        }
        let width = i == highlightRow ? row.width : row.width * [0.92, 0.7, 0.84, 0.6, 0.76][i]
        NSBezierPath(roundedRect: NSRect(x: row.minX, y: row.minY + 8, width: width, height: 18),
                     xRadius: 9, yRadius: 9).fill()
    }
}

let panelW: CGFloat = 318, panelH: CGFloat = 470, gap: CGFloat = 44
let panelY: CGFloat = 230
let leftX = (size - (panelW * 2 + gap)) / 2
panel(NSRect(x: leftX, y: panelY, width: panelW, height: panelH), highlightRow: 1, alpha: 0.97)
panel(NSRect(x: leftX + panelW + gap, y: panelY, width: panelW, height: panelH), highlightRow: nil, alpha: 0.90)

// Amber arc arrow from the left panel's highlighted row over to the right panel.
ctx.saveGState()
let arrowShadow = NSShadow()
arrowShadow.shadowColor = color(0x7C2D12, 0.45)
arrowShadow.shadowOffset = NSSize(width: 0, height: -6)
arrowShadow.shadowBlurRadius = 14
arrowShadow.set()
let start = NSPoint(x: leftX + panelW * 0.62, y: panelY + panelH + 6)
let end = NSPoint(x: leftX + panelW + gap + panelW * 0.40, y: panelY + panelH + 6)
let arc = NSBezierPath()
arc.move(to: start)
arc.curve(to: end, controlPoint1: NSPoint(x: start.x + 40, y: start.y + 150),
          controlPoint2: NSPoint(x: end.x - 40, y: end.y + 150))
arc.lineWidth = 34
arc.lineCapStyle = .round
color(0xFBBF24).setStroke()
arc.stroke()
// Arrow head pointing down into the right panel.
let head = NSBezierPath()
head.move(to: NSPoint(x: end.x - 52, y: end.y + 40))
head.line(to: NSPoint(x: end.x + 4, y: end.y - 34))
head.line(to: NSPoint(x: end.x + 56, y: end.y + 44))
head.close()
color(0xFBBF24).setFill()
head.fill()
ctx.restoreGState()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
print("wrote \(out)")
