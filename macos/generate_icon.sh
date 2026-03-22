#!/bin/bash
set -e

# Generate a simple app icon using a rendered SF Symbol
ICONSET_DIR="build/WhisperGate.app/Contents/Resources/AppIcon.iconset"
ICNS_FILE="build/WhisperGate.app/Contents/Resources/AppIcon.icns"

mkdir -p "$ICONSET_DIR"

# Create a 1024x1024 base icon using a Swift one-liner
swift - <<'SWIFT'
import AppKit

let size: CGFloat = 1024
let image = NSImage(size: NSSize(width: size, height: size))
image.lockFocus()

let ctx = NSGraphicsContext.current!.cgContext

// Dark rounded rect background
let inset = size * 0.05
let rect = CGRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
let path = CGPath(roundedRect: rect, cornerWidth: size * 0.22, cornerHeight: size * 0.22, transform: nil)
ctx.addPath(path)
ctx.clip()

// Gradient background
let colors = [
    CGColor(red: 0.10, green: 0.10, blue: 0.16, alpha: 1),
    CGColor(red: 0.05, green: 0.05, blue: 0.10, alpha: 1),
]
let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: size), end: CGPoint(x: 0, y: 0), options: [])

// Subtle ring
let ringInset = size * 0.22
let ringRect = CGRect(x: ringInset, y: ringInset, width: size - ringInset * 2, height: size - ringInset * 2)
ctx.setStrokeColor(CGColor(red: 0.3, green: 0.55, blue: 1.0, alpha: 0.25))
ctx.setLineWidth(size * 0.015)
ctx.strokeEllipse(in: ringRect)

// Draw mic symbol using simple geometry (more reliable than SF Symbols for icon gen)
let cx = size / 2
let cy = size / 2 + size * 0.02

// Mic body
let micW = size * 0.12
let micH = size * 0.22
let micRect = CGRect(x: cx - micW/2, y: cy - micH * 0.1, width: micW, height: micH)
let micPath = CGPath(roundedRect: micRect, cornerWidth: micW/2, cornerHeight: micW/2, transform: nil)
ctx.setFillColor(CGColor(red: 0.35, green: 0.65, blue: 1.0, alpha: 1.0))
ctx.addPath(micPath)
ctx.fillPath()

// Mic arc (U shape below mic)
let arcRadius = size * 0.14
let arcCenter = CGPoint(x: cx, y: cy + micH * 0.15)
ctx.setStrokeColor(CGColor(red: 0.35, green: 0.65, blue: 1.0, alpha: 1.0))
ctx.setLineWidth(size * 0.025)
ctx.setLineCap(.round)
ctx.addArc(center: arcCenter, radius: arcRadius, startAngle: .pi * 0.15, endAngle: .pi * 0.85, clockwise: true)
ctx.strokePath()

// Mic stand
let standTop = arcCenter.y - arcRadius
let standBottom = standTop - size * 0.08
ctx.move(to: CGPoint(x: cx, y: standTop))
ctx.addLine(to: CGPoint(x: cx, y: standBottom))
ctx.strokePath()

// Stand base
let baseW = size * 0.1
ctx.move(to: CGPoint(x: cx - baseW/2, y: standBottom))
ctx.addLine(to: CGPoint(x: cx + baseW/2, y: standBottom))
ctx.strokePath()

// Waveform bars on sides
let barColor = CGColor(red: 0.35, green: 0.65, blue: 1.0, alpha: 0.6)
ctx.setFillColor(barColor)
let barW = size * 0.025
let barHeights: [CGFloat] = [0.06, 0.12, 0.18, 0.14, 0.08]
for (i, h) in barHeights.enumerated() {
    let bh = size * h
    let offset = size * 0.16 + CGFloat(i) * size * 0.045
    // Left side
    ctx.fill(CGRect(x: cx - offset - barW/2, y: cy - bh/2, width: barW, height: bh))
    // Right side
    ctx.fill(CGRect(x: cx + offset - barW/2, y: cy - bh/2, width: barW, height: bh))
}

image.unlockFocus()

// Save
guard let tiff = image.tiffRepresentation,
      let rep = NSBitmapImageRep(data: tiff),
      let png = rep.representation(using: .png, properties: [:]) else {
    print("Failed")
    exit(1)
}
try! png.write(to: URL(fileURLWithPath: "build/icon_base.png"))
print("Base icon created")
SWIFT

# Generate all required sizes from the base image
sizes=("16x16" "32x32" "128x128" "256x256" "512x512")

for s in "${sizes[@]}"; do
    dim="${s%x*}"
    dim2=$((dim * 2))
    sips -z "$dim" "$dim" build/icon_base.png --out "$ICONSET_DIR/icon_${s}.png" >/dev/null 2>&1
    sips -z "$dim2" "$dim2" build/icon_base.png --out "$ICONSET_DIR/icon_${s}@2x.png" >/dev/null 2>&1
done

# Convert to icns
iconutil -c icns "$ICONSET_DIR" -o "$ICNS_FILE"
rm -rf "$ICONSET_DIR" build/icon_base.png

echo "AppIcon.icns created"
