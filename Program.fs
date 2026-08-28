module QrLinkPdf.Program

open System
open System.IO
open PDFtoImage
open SkiaSharp
open ZXing
open ZXing.SkiaSharp
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Action
open iText.Kernel.Pdf.Annot
open iText.Kernel.Geom

/// DPI used to rasterize each page for QR scanning. Higher = better detection
/// of small/dense codes, slower and more memory.
let private scanDpi = 400

/// Set QRLINK_DEBUG=1 to dump each rasterized page as a PNG and log every
/// QR code ZXing finds, including ones whose payload isn't a URL.
let private debug = Environment.GetEnvironmentVariable("QRLINK_DEBUG") = "1"

/// One QR code found on a page, in PDF user-space coordinates (points,
/// origin bottom-left) ready to drop straight into a link annotation.
type private FoundCode =
    { PageNumber: int // 1-based
      Text: string
      Rect: Rectangle }

/// Does the decoded QR payload look like something worth hyperlinking?
/// iText's PdfAction.CreateURI just needs an absolute URI; we also allow
/// bare "www." text and upgrade it to https.
let private toUri (text: string) : string option =
    let text = text.Trim()
    if Uri.IsWellFormedUriString(text, UriKind.Absolute) then
        Some text
    elif text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) then
        Some("https://" + text)
    else
        None

let private makeReader () =
    let reader = BarcodeReaderGeneric()
    reader.Options.PossibleFormats <- [| BarcodeFormat.QR_CODE |]
    reader.Options.TryHarder <- true
    reader.Options.PureBarcode <- false
    reader.AutoRotate <- true
    reader

/// Turn a ZXing result's finder-pattern corner points into a padded
/// pixel-space box (the corner points alone aren't a tight box around the
/// full symbol), rescaled from a decode done on a resized copy of the page
/// back into full-resolution page coordinates.
let private boxFromResult (inverseScale: float32) (r: Result) : (string * SKRectI) option =
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
        Some(r.Text, SKRectI(left, top, right, bottom))

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
let private decodeWholeImage (inverseScale: float32) (bitmap: SKBitmap) : (string * SKRectI) list =
    let reader = makeReader ()
    let luminance = SKBitmapLuminanceSource(bitmap)
    match reader.DecodeMultiple(luminance) with
    | null -> []
    | results -> results |> Array.choose (boxFromResult inverseScale) |> Array.toList

/// Scan one rasterized page bitmap for QR codes with ZXing, the way a phone
/// camera scanner does: decode at several resolutions and merge whatever
/// each one finds, rather than trusting a single pass.
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
let private findQrCodesOnBitmap (bitmap: SKBitmap) : (string * SKRectI) list =
    let found = ResizeArray<string * SKRectI>()

    let tryAdd (text: string, box: SKRectI) =
        let isDuplicate = found |> Seq.exists (fun (t, r) -> t = text && sameSpot r box)
        if not isDuplicate then found.Add(text, box)

    for scale in [ 1.0; 0.6; 0.4; 0.25 ] do
        let width = max 1 (int (float bitmap.Width * scale))
        let height = max 1 (int (float bitmap.Height * scale))
        use level =
            if scale = 1.0 then
                bitmap.Copy()
            else
                bitmap.Resize(SKImageInfo(width, height), SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
        let inverseScale = float32 bitmap.Width / float32 level.Width
        decodeWholeImage inverseScale level |> List.iter tryAdd

    if debug then
        eprintfn "  [debug] %dx%d bitmap -> %d unique code(s)" bitmap.Width bitmap.Height found.Count
        for text, box in found do
            eprintfn "    %A -> %s" box text

    found |> List.ofSeq

/// Rasterize every page of the PDF and scan each for QR codes, returning
/// their locations translated into PDF point coordinates for that page.
let private findAllQrCodes (pdfBytes: byte[]) (pageSizes: Map<int, float * float>) : FoundCode list =
    let options = RenderOptions(Dpi = scanDpi, WithAnnotations = false, WithFormFill = false)
    Conversion.ToImages(pdfBytes, options = options)
    |> Seq.indexed
    |> Seq.collect (fun (i, bitmap) ->
        use bitmap = bitmap
        let pageNumber = i + 1
        let pageWidthPt, pageHeightPt = pageSizes.[pageNumber]
        // Pixels-per-point on this render, so we can map bitmap coordinates
        // back to the PDF's own coordinate space.
        let scaleX = pageWidthPt / float bitmap.Width
        let scaleY = pageHeightPt / float bitmap.Height

        findQrCodesOnBitmap bitmap
        |> List.choose (fun (text, box) ->
            match toUri text with
            | None -> None
            | Some uri ->
                let llx = float box.Left * scaleX
                let urx = float box.Right * scaleX
                // Image Y grows downward from the top; PDF Y grows upward
                // from the bottom, so flip here.
                let ury = pageHeightPt - float box.Top * scaleY
                let lly = pageHeightPt - float box.Bottom * scaleY
                Some
                    { PageNumber = pageNumber
                      Text = uri
                      Rect = Rectangle(float32 llx, float32 lly, float32 (urx - llx), float32 (ury - lly)) })
        |> List.ofSeq)
    |> List.ofSeq

/// Add a clickable (invisible border) link annotation over `rect` on `page`
/// pointing at `uri`.
let private addLinkAnnotation (page: PdfPage) (rect: Rectangle) (uri: string) =
    let action = PdfAction.CreateURI(uri)
    let annotation = PdfLinkAnnotation(rect).SetAction(action)
    // Zero-width, invisible border so the added link doesn't draw a visible
    // box over the QR code image.
    annotation.SetBorder(PdfArray([| 0; 0; 0 |])) |> ignore
    page.AddAnnotation(annotation) |> ignore

[<EntryPoint>]
let main argv =
    match argv with
    | [| inputPath; outputPath |] ->
        let pdfBytes = File.ReadAllBytes(inputPath)

        use reader = new PdfReader(inputPath)
        use writer = new PdfWriter(outputPath)
        use doc = new PdfDocument(reader, writer)

        let pageSizes =
            [ for i in 1 .. doc.GetNumberOfPages() ->
                  let size = doc.GetPage(i).GetPageSize()
                  i, (float (size.GetWidth()), float (size.GetHeight())) ]
            |> Map.ofList

        let found = findAllQrCodes pdfBytes pageSizes

        if found.IsEmpty then
            printfn "No QR codes with URL/URI payloads were found."
        else
            for code in found do
                printfn "Page %d: linking QR code to %s" code.PageNumber code.Text
                let page = doc.GetPage(code.PageNumber)
                addLinkAnnotation page code.Rect code.Text

        printfn "Wrote %s (%d link%s added)" outputPath found.Length (if found.Length = 1 then "" else "s")
        0
    | _ ->
        eprintfn "Usage: QrLinkPdf <input.pdf> <output.pdf>"
        1
