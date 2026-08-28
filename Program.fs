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
let private scanDpi = 200

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

/// Scan one rasterized page bitmap for QR codes with ZXing, returning the
/// decoded text plus a pixel-space bounding box for each one found.
let private findQrCodesOnBitmap (bitmap: SKBitmap) : (string * SKRectI) list =
    let reader = BarcodeReaderGeneric()
    reader.Options.PossibleFormats <- [| BarcodeFormat.QR_CODE |]
    reader.Options.TryHarder <- true
    reader.AutoRotate <- true

    let luminance = SKBitmapLuminanceSource(bitmap)
    let results = reader.DecodeMultiple(luminance)

    if isNull results then
        []
    else
        results
        |> Array.choose (fun r ->
            let points = r.ResultPoints
            if isNull points || points.Length = 0 then
                None
            else
                let xs = points |> Array.map (fun p -> p.X)
                let ys = points |> Array.map (fun p -> p.Y)
                // ResultPoints for a QR code are its finder-pattern corners,
                // not a tight box around the full symbol - pad generously
                // so the clickable area covers the whole code.
                let minX, maxX = Array.min xs, Array.max xs
                let minY, maxY = Array.min ys, Array.max ys
                let width = maxX - minX
                let height = maxY - minY
                let pad = 0.20f * max width height |> max 4.0f
                let left = int (minX - pad) |> max 0
                let top = int (minY - pad) |> max 0
                let right = int (maxX + pad) |> min (bitmap.Width - 1)
                let bottom = int (maxY + pad) |> min (bitmap.Height - 1)
                Some(r.Text, SKRectI(left, top, right, bottom)))
        |> Array.toList

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
