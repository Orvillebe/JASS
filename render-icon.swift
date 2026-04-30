#!/usr/bin/env swift
//
// render-icon.swift
// Renders icon.svg to PNG files at the sizes macOS needs for an .icns bundle.
//
// Usage:  swift render-icon.swift <source.svg> <output-dir>
//
// Sizes produced (Apple's iconutil spec):
//   icon_16x16.png
//   icon_16x16@2x.png   (32x32)
//   icon_32x32.png
//   icon_32x32@2x.png   (64x64)
//   icon_128x128.png
//   icon_128x128@2x.png (256x256)
//   icon_256x256.png
//   icon_256x256@2x.png (512x512)
//   icon_512x512.png
//   icon_512x512@2x.png (1024x1024)

import Foundation
import AppKit

// MARK: - Entry

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write("Usage: render-icon.swift <source.svg> <output-dir>\n".data(using: .utf8)!)
    exit(1)
}

let svgPath = CommandLine.arguments[1]
let outDir  = CommandLine.arguments[2]

let svgURL = URL(fileURLWithPath: svgPath)
let outURL = URL(fileURLWithPath: outDir)

// Ensure the output directory exists.
try? FileManager.default.createDirectory(at: outURL, withIntermediateDirectories: true)

// Load the SVG as NSImage. AppKit on macOS 14+ can render SVGs natively.
guard let image = NSImage(contentsOf: svgURL) else {
    FileHandle.standardError.write("Failed to load SVG from \(svgPath)\n".data(using: .utf8)!)
    exit(1)
}

// Render each required size.
//
// Each tuple is (logical size, scale factor, filename). iconutil needs both
// non-retina and retina variants at every logical size.
let targets: [(Int, Int, String)] = [
    (16,   1, "icon_16x16.png"),
    (16,   2, "icon_16x16@2x.png"),
    (32,   1, "icon_32x32.png"),
    (32,   2, "icon_32x32@2x.png"),
    (128,  1, "icon_128x128.png"),
    (128,  2, "icon_128x128@2x.png"),
    (256,  1, "icon_256x256.png"),
    (256,  2, "icon_256x256@2x.png"),
    (512,  1, "icon_512x512.png"),
    (512,  2, "icon_512x512@2x.png"),
]

/// Render the source image at `pixels` x `pixels` and write as PNG.
func renderAndWrite(pixels: Int, to url: URL) throws {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: pixels,
        pixelsHigh: pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    )
    guard let rep else {
        throw NSError(domain: "render-icon", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "Could not create bitmap rep for \(pixels)px"])
    }
    rep.size = NSSize(width: pixels, height: pixels)

    NSGraphicsContext.saveGraphicsState()
    defer { NSGraphicsContext.restoreGraphicsState() }
    guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else {
        throw NSError(domain: "render-icon", code: 2,
                      userInfo: [NSLocalizedDescriptionKey: "Could not create graphics context for \(pixels)px"])
    }
    NSGraphicsContext.current = ctx
    ctx.imageInterpolation = .high

    image.draw(
        in: NSRect(x: 0, y: 0, width: pixels, height: pixels),
        from: .zero,
        operation: .copy,
        fraction: 1.0
    )

    guard let data = rep.representation(using: .png, properties: [:]) else {
        throw NSError(domain: "render-icon", code: 3,
                      userInfo: [NSLocalizedDescriptionKey: "Could not encode PNG for \(pixels)px"])
    }
    try data.write(to: url)
}

do {
    for (logical, scale, name) in targets {
        let pixels = logical * scale
        let fileURL = outURL.appendingPathComponent(name)
        try renderAndWrite(pixels: pixels, to: fileURL)
        print("wrote \(name) (\(pixels)x\(pixels))")
    }
} catch {
    FileHandle.standardError.write("Render failed: \(error.localizedDescription)\n".data(using: .utf8)!)
    exit(1)
}
