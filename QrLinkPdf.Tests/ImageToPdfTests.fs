/// Real end-to-end coverage for ImageToPdf.convert: build a plain image (no
/// PDF involved at all) with both a QR code and some printed text, wrap it
/// with ImageToPdf, and feed the result through the real PdfQrLinker
/// pipeline - including a real TesseractEngine, same as OcrTests.fs (see
/// its own comment for why fake engines aren't the right tool here) - to
/// confirm the whole "upload a photo" path actually works, not just that
/// the PDF byte-wrapping compiles.
module QrLinkPdf.Tests.ImageToPdfTests

open System
open System.IO
open SkiaSharp
open Tesseract
open AnyUnit.Style.FSharp.Test
open AnyUnit.Style.Xunit
open QrLinkPdf
open QrLinkPdf.Tests.TestPdfs

let private tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata")

let ``a photographed-looking image finds both its QR code and its printed text URL`` () = test {
    let! Assert = assertion
    if not (File.Exists(Path.Combine(tessdataPath, "eng.traineddata"))) then
        failwith $"Missing {tessdataPath}/eng.traineddata - see README's OCR setup."

    // A plain image, built the way a phone photo of a flyer would look:
    // some printed text up top, a QR code lower down - nothing about this
    // ever touches a PdfDocument until ImageToPdf.convert does.
    let width, height = 800, 1000
    use bitmap = new SKBitmap(width, height)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear(SKColors.White)

    use paint = new SKPaint(Color = SKColors.Black, IsAntialias = true)
    use font = new SKFont(SKTypeface.Default, 36f)
    canvas.DrawText("Visit https://example.com/photo-upload today", 20f, 60f, SKTextAlign.Left, font, paint)

    use qrBitmap = SKBitmap.Decode(qrPng "https://example.com/photo-qr" 400)
    canvas.DrawBitmap(qrBitmap, SKRect(200f, 400f, 600f, 800f), SKSamplingOptions())
    canvas.Flush()

    use image = SKImage.FromBitmap(bitmap)
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    let pdfBytes = ImageToPdf.convert (data.ToArray())

    use engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default)

    let options =
        { ScanOptions.Default with
            Dpi = 300
            Scales = [ 1.0 ]
            OcrEngine = Some(TesseractWords.ofBitmap engine) }

    use input = new MemoryStream(pdfBytes)
    let found = (PdfQrLinker.scan options input).Links |> List.map (fun l -> l.Uri) |> List.sort

    Assert.Equal<string list>([ "https://example.com/photo-qr"; "https://example.com/photo-upload" ], found)
}
