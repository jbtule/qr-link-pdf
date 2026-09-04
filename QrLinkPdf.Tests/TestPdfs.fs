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

/// A QR code to place on a generated page. Position is in PDF points from the
/// bottom-left of the page - the same space QrLink reports - so a test can
/// compare what it asked for against what was found.
type Placement =
    { Payload: string
      Left: float32
      Bottom: float32
      Size: float32 }

/// A comfortably large code in the lower-left area of a page.
let placement payload =
    { Payload = payload
      Left = 72f
      Bottom = 72f
      Size = 160f }

let at (left, bottom) p = { p with Left = left; Bottom = bottom }

let sized size p = { p with Size = size }

/// Render a QR payload to PNG bytes. The margin is the quiet zone in modules;
/// ZXing needs some to detect reliably.
let qrPng (payload: string) (pixels: int) =
    let writer = ZXing.SkiaSharp.BarcodeWriter(Format = ZXing.BarcodeFormat.QR_CODE)
    writer.Options <- ZXing.Common.EncodingOptions(Width = pixels, Height = pixels, Margin = 1)
    use bitmap = writer.Write(payload)
    use image = SKImage.FromBitmap(bitmap)
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

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
            let data = iText.IO.Image.ImageDataFactory.Create(qrPng p.Payload 600)

            Image(data).SetFixedPosition(pageNumber, p.Left, p.Bottom, UnitValue.CreatePointValue p.Size)
            |> doc.Add
            |> ignore)

    doc.Close()
    output.ToArray()

/// One letter page carrying the given placements.
let build (placements: Placement list) = buildPages PageSize.LETTER [ placements ]

/// One letter page with a single code at the default position.
let single (payload: string) = build [ placement payload ]
