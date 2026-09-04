/// Finding QR codes in a bitmap. Knows nothing about PDFs - it takes an
/// image and reports what it decoded and where.
module QrLinkPdf.Scanner

open SkiaSharp
open ZXing
open ZXing.SkiaSharp

/// A decoded code and its bounding box in the source bitmap's pixel space
/// (origin top-left).
type DecodedCode = { Text: string; Box: SKRectI }

let private makeReader () =
    let reader = BarcodeReaderGeneric()
    reader.Options.PossibleFormats <- [| BarcodeFormat.QR_CODE |]
    reader.Options.TryHarder <- true
    reader.Options.PureBarcode <- false
    // Deliberately NOT AutoRotate. QR detection is already rotation-invariant
    // (the finder patterns carry the orientation), so it finds nothing extra -
    // but when a code decodes only on the rotated pass, ZXing reports its
    // corner points in the *rotated* image's coordinates, which we would then
    // map as if they were upright. That produces a phantom link at a
    // transposed position, sometimes off the page entirely.
    reader

/// Turn a ZXing result's finder-pattern corner points into a padded
/// pixel-space box (the corner points alone aren't a tight box around the
/// full symbol), rescaled from a decode done on a resized copy of the page
/// back into full-resolution page coordinates.
let private boxFromResult (inverseScale: float32) (r: Result) : DecodedCode option =
    let points = r.ResultPoints

    if isNull points || points.Length = 0 then
        None
    else
        let xs = points |> Array.map (fun p -> p.X)
        let ys = points |> Array.map (fun p -> p.Y)
        let minX, maxX = Array.min xs, Array.max xs
        let minY, maxY = Array.min ys, Array.max ys
        let width = maxX - minX
        let height = maxY - minY
        let pad = 0.20f * max width height |> max 4.0f
        let left = int ((minX - pad) * inverseScale)
        let top = int ((minY - pad) * inverseScale)
        let right = int ((maxX + pad) * inverseScale)
        let bottom = int ((maxY + pad) * inverseScale)
        Some { Text = r.Text; Box = SKRectI(left, top, right, bottom) }

/// Do two detected boxes plausibly refer to the same physical QR code?
let private sameSpot (a: SKRectI) (b: SKRectI) =
    let intersection = SKRectI.Intersect(a, b)

    if intersection.IsEmpty then
        false
    else
        let overlapArea = float (intersection.Width * intersection.Height)
        let smaller = float (min (a.Width * a.Height) (b.Width * b.Height))
        smaller > 0.0 && overlapArea / smaller > 0.5

/// Decode every QR code ZXing can find in the bitmap, reporting boxes
/// rescaled by `inverseScale` (pass 1.0 if `bitmap` is already full-size).
let private decodeWholeImage (inverseScale: float32) (bitmap: SKBitmap) : DecodedCode list =
    let reader = makeReader ()
    let luminance = SKBitmapLuminanceSource(bitmap)

    match reader.DecodeMultiple(luminance) with
    | null -> []
    | results -> results |> Array.choose (boxFromResult inverseScale) |> Array.toList

/// Scan one bitmap for QR codes with ZXing, the way a phone camera scanner
/// does: decode at several resolutions and merge whatever each one finds,
/// rather than trusting a single pass.
///
/// This matters because ZXing's detector can silently miss codes depending
/// on how large they are relative to the full image - a code that's a small
/// fraction of a big page can get lost among unrelated ink, and (less
/// intuitively) a code that fills a big fraction of a large page can *also*
/// fail against the full-resolution canvas despite decoding fine once
/// shrunk down. A phone camera never hits this because the user's framing
/// keeps the code at a middling size in the actual pixels the sensor reads;
/// scanning a fixed-size page render has no such guarantee, so instead we
/// build a small image pyramid and scan every level.
let findOnBitmap (options: ScanOptions) (bitmap: SKBitmap) : DecodedCode list =
    let found = ResizeArray<DecodedCode>()

    let tryAdd (code: DecodedCode) =
        let isDuplicate =
            found |> Seq.exists (fun c -> c.Text = code.Text && sameSpot c.Box code.Box)

        if not isDuplicate then found.Add(code)

    for scale in options.Scales do
        let width = max 1 (int (float bitmap.Width * scale))
        let height = max 1 (int (float bitmap.Height * scale))

        use level =
            if scale = 1.0 then
                bitmap.Copy()
            else
                bitmap.Resize(SKImageInfo(width, height), SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))

        let inverseScale = float32 bitmap.Width / float32 level.Width
        decodeWholeImage inverseScale level |> List.iter tryAdd

    options.Trace(sprintf "  %dx%d bitmap -> %d unique code(s)" bitmap.Width bitmap.Height found.Count)

    for code in found do
        options.Trace(sprintf "    %A -> %s" code.Box code.Text)

    found |> List.ofSeq
