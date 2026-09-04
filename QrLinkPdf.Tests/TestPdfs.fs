/// Builds PDFs containing QR codes at known positions, so tests can assert on
/// what was found and where without shipping any real-world documents.
module QrLinkPdf.Tests.TestPdfs

open System.IO
open SkiaSharp
open iText.Kernel.Geom
open iText.Kernel.Pdf
open iText.Layout
open iText.Layout.Element
open iText.Layout.Properties

/// Ways to rough up a generated code before it goes on the page. Crisp
/// fixtures never exercise the multi-scale scan in Scanner.fs, which exists
/// precisely for input that has been through a printer and a scanner.
type Degradation =
    /// Straight from the encoder.
    | Crisp
    /// Rotated about its centre, as if the page were fed in skew.
    | Rotated of degrees: float32
    /// Shrunk and stretched back, which smears the module edges.
    | Resampled of factor: float32
    /// Contrast pulled toward mid grey, as if photocopied.
    | Faded of strength: float32
    /// Re-encoded as JPEG, which rings around the high-contrast edges.
    | JpegArtifacts of quality: int

/// A QR code to place on a generated page. Position is in PDF points from the
/// bottom-left of the page - the same space QrLink reports - so a test can
/// compare what it asked for against what was found.
type Placement =
    { Payload: string
      Left: float32
      Bottom: float32
      Size: float32
      Degradation: Degradation }

/// A comfortably large code in the lower-left area of a page.
let placement payload =
    { Payload = payload
      Left = 72f
      Bottom = 72f
      Size = 160f
      Degradation = Crisp }

let at (left, bottom) p = { p with Left = left; Bottom = bottom }

let sized size p = { p with Size = size }

let degraded degradation p = { p with Degradation = degradation }

/// Apply a degradation to a freshly encoded code. Takes ownership of `bitmap`.
let private applyDegradation degradation (bitmap: SKBitmap) : SKBitmap =
    match degradation with
    | Crisp -> bitmap

    | Rotated degrees ->
        // Room for the corners to swing out, on white so the quiet zone survives.
        let size = int (float32 (max bitmap.Width bitmap.Height) * 1.5f)
        let result = new SKBitmap(size, size)
        use canvas = new SKCanvas(result)
        canvas.Clear(SKColors.White)
        canvas.Translate(float32 size / 2f, float32 size / 2f)
        canvas.RotateDegrees(degrees)
        canvas.Translate(float32 bitmap.Width / -2f, float32 bitmap.Height / -2f)
        use source = SKImage.FromBitmap(bitmap)
        canvas.DrawImage(source, SKPoint(0f, 0f), SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
        bitmap.Dispose()
        result

    | Resampled factor ->
        let small = max 1 (int (float32 bitmap.Width * factor))
        let sampling = SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
        use shrunk = bitmap.Resize(SKImageInfo(small, small), sampling)
        let result = shrunk.Resize(SKImageInfo(bitmap.Width, bitmap.Height), sampling)
        bitmap.Dispose()
        result

    | Faded strength ->
        // Pull every channel toward mid grey. Done by hand rather than with a
        // colour filter so it behaves identically across SkiaSharp versions.
        let keep = 1f - strength

        for x in 0 .. bitmap.Width - 1 do
            for y in 0 .. bitmap.Height - 1 do
                let c = bitmap.GetPixel(x, y)
                let fade (channel: byte) =
                    byte (128f * strength + float32 channel * keep)

                bitmap.SetPixel(x, y, SKColor(fade c.Red, fade c.Green, fade c.Blue, c.Alpha))

        bitmap

    | JpegArtifacts _ -> bitmap

/// Encode a code to image bytes, degraded as asked. JPEG for the artifact case,
/// PNG otherwise; iText reads both.
let qrImage (payload: string) (pixels: int) (degradation: Degradation) =
    let writer = ZXing.SkiaSharp.BarcodeWriter(Format = ZXing.BarcodeFormat.QR_CODE)
    writer.Options <- ZXing.Common.EncodingOptions(Width = pixels, Height = pixels, Margin = 1)

    use bitmap = applyDegradation degradation (writer.Write(payload))
    use image = SKImage.FromBitmap(bitmap)

    use data =
        match degradation with
        | JpegArtifacts quality -> image.Encode(SKEncodedImageFormat.Jpeg, quality)
        | _ -> image.Encode(SKEncodedImageFormat.Png, 100)

    data.ToArray()

/// Render a QR payload to crisp PNG bytes. The margin is the quiet zone in
/// modules; ZXing needs some to detect reliably.
let qrPng (payload: string) (pixels: int) = qrImage payload pixels Crisp

/// Build a PDF from one list of placements per page. Each page also gets a line
/// of text, so the fixtures aren't suspiciously blank pages of pure QR.
let buildPages (pageSize: PageSize) (pages: Placement list list) : byte[] =
    let output = new MemoryStream()
    let writer = new PdfWriter(output)
    // Keep `output` usable after Close.
    writer.SetCloseStream(false)
    let pdf = new PdfDocument(writer)
    let doc = new Document(pdf, pageSize)

    pages
    |> List.iteri (fun index placements ->
        if index > 0 then
            doc.Add(AreaBreak(AreaBreakType.NEXT_PAGE)) |> ignore

        let pageNumber = index + 1
        doc.Add(Paragraph(sprintf "Generated test page %d" pageNumber)) |> ignore

        for p in placements do
            let data = iText.IO.Image.ImageDataFactory.Create(qrImage p.Payload 600 p.Degradation)

            Image(data).SetFixedPosition(pageNumber, p.Left, p.Bottom, UnitValue.CreatePointValue p.Size)
            |> doc.Add
            |> ignore)

    doc.Close()
    output.ToArray()

/// One letter page carrying the given placements.
let build (placements: Placement list) = buildPages PageSize.LETTER [ placements ]

/// One letter page with a single code at the default position.
let single (payload: string) = build [ placement payload ]
