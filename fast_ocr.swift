// fast_ocr — reads the 16 letters of a WordLink board from the screen.
//
// Usage: fast_ocr x y width height [scale]
//
// Takes a full-screen screenshot (same native resolution the HUD calibrated
// against), crops the saved board rectangle, splits it into a uniform 4x4
// grid, and runs Apple's Vision text recognizer on each cell's letter zone
// (the bottom of every tile is skipped so the point-value dots are never
// read). Prints two lines: the 16 letters ('?' for unreadable cells) and the
// 16 confidences.
//
// Build: swiftc -O fast_ocr.swift -o fast_ocr

import AppKit
import CoreGraphics
import Foundation
import ImageIO
import Vision

// Letter zone inside each tile, as fractions of the cell box.
let cellInsetX: CGFloat = 0.18
let cellInsetTop: CGFloat = 0.05
let cellLetterHeight: CGFloat = 0.68

struct CellRead {
    let letter: String
    let confidence: Float
}

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

func parseDouble(_ value: String, _ name: String) -> Double {
    guard let parsed = Double(value) else {
        fail("invalid \(name): \(value)")
    }
    return parsed
}

/// Maps a raw Vision candidate to a single board letter, or nil.
/// The board only contains A-Z, so common lookalikes are corrected.
func boardLetter(from raw: String) -> String? {
    let upper = raw.uppercased()
    for character in upper {
        if character >= "A" && character <= "Z" {
            return String(character)
        }
    }
    if upper.contains("0") { return "O" }
    if upper.contains("1") || upper.contains("|") || upper.contains("!") { return "I" }
    if upper.contains("5") { return "S" }
    if upper.contains("8") { return "B" }
    if upper.contains("2") { return "Z" }
    return nil
}

func recognizeLetter(in image: CGImage) -> CellRead {
    let handler = VNImageRequestHandler(cgImage: image, options: [:])
    var best = CellRead(letter: "?", confidence: 0)

    let request = VNRecognizeTextRequest { request, _ in
        guard let observations = request.results as? [VNRecognizedTextObservation] else {
            return
        }
        for observation in observations {
            for candidate in observation.topCandidates(3) {
                guard let letter = boardLetter(from: candidate.string) else { continue }
                if candidate.confidence > best.confidence {
                    best = CellRead(letter: letter, confidence: candidate.confidence)
                }
            }
        }
    }
    // Fast pass, no dictionary correction (single letters are not words),
    // and the glyph fills most of the cell crop.
    request.recognitionLevel = .fast
    request.usesLanguageCorrection = false
    request.minimumTextHeight = 0.25

    do {
        try handler.perform([request])
    } catch {
        return best
    }
    return best
}

func savePNG(_ image: CGImage, to path: String) {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let destination = CGImageDestinationCreateWithURL(url, "public.png" as CFString, 1, nil) else {
        return
    }
    CGImageDestinationAddImage(destination, image, nil)
    CGImageDestinationFinalize(destination)
}

// ---------------------------------------------------------------------------
// Arguments
// ---------------------------------------------------------------------------

let args = CommandLine.arguments
guard args.count == 5 || args.count == 6 else {
    fail("usage: fast_ocr x y width height [scale]")
}

let x = parseDouble(args[1], "x")
let y = parseDouble(args[2], "y")
let width = parseDouble(args[3], "width")
let height = parseDouble(args[4], "height")
let scale = args.count == 6
    ? parseDouble(args[5], "scale")
    : Double(NSScreen.main?.backingScaleFactor ?? 2.0)

let boardRect = CGRect(
    x: x * scale,
    y: y * scale,
    width: width * scale,
    height: height * scale
).integral

// ---------------------------------------------------------------------------
// Capture
// ---------------------------------------------------------------------------

let shotURL = URL(fileURLWithPath: NSTemporaryDirectory())
    .appendingPathComponent("wordlink-ocr-\(UUID().uuidString).png")

let capture = Process()
capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
capture.arguments = ["-x", shotURL.path]

do {
    try capture.run()
    capture.waitUntilExit()
} catch {
    fail("screen capture failed: \(error.localizedDescription)")
}
guard capture.terminationStatus == 0 else {
    fail("screen capture failed; grant Screen Recording permission and retry")
}

guard let screenshot = NSImage(contentsOf: shotURL) else {
    try? FileManager.default.removeItem(at: shotURL)
    fail("captured image could not be opened")
}
var proposedRect = CGRect(origin: .zero, size: screenshot.size)
guard let fullImage = screenshot.cgImage(forProposedRect: &proposedRect, context: nil, hints: nil) else {
    try? FileManager.default.removeItem(at: shotURL)
    fail("captured image could not be decoded")
}
try? FileManager.default.removeItem(at: shotURL)

guard let boardImage = fullImage.cropping(to: boardRect) else {
    fail("calibrated crop is outside the captured screen — recalibrate")
}

if let debugPath = ProcessInfo.processInfo.environment["WORDLINK_DEBUG_CROP"], !debugPath.isEmpty {
    try? FileManager.default.removeItem(atPath: debugPath)
    savePNG(boardImage, to: debugPath)
}

// ---------------------------------------------------------------------------
// Uniform 4x4 split + per-cell recognition
// ---------------------------------------------------------------------------

let cellWidth = CGFloat(boardImage.width) / 4.0
let cellHeight = CGFloat(boardImage.height) / 4.0
let insetX = cellWidth * cellInsetX
let insetTop = cellHeight * cellInsetTop
let letterHeight = cellHeight * cellLetterHeight

var letters: [String] = []
var confidences: [String] = []

for row in 0..<4 {
    for col in 0..<4 {
        let rect = CGRect(
            x: CGFloat(col) * cellWidth + insetX,
            y: CGFloat(row) * cellHeight + insetTop,
            width: cellWidth - insetX * 2.0,
            height: letterHeight
        ).integral
        guard let cellImage = boardImage.cropping(to: rect) else {
            letters.append("?")
            confidences.append("0.00")
            continue
        }
        let read = recognizeLetter(in: cellImage)
        letters.append(read.letter)
        confidences.append(String(format: "%.2f", read.confidence))
    }
}

print(letters.joined())
print(confidences.joined(separator: " "))
