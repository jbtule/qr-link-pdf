/// Wraps a raw image (PNG/JPEG/etc. - whatever iText's own ImageDataFactory
/// can sniff from the bytes) in a one-page PDF, sized to the image's own
/// pixel dimensions. Lets a photographed/scanned image feed straight into
/// PdfQrLinker.scan/link exactly like any other PDF - the whole point being
/// that an image page has no real text-showing operators at all, the same
/// "flattened to vector outlines" shape TextLinker's OCR fallback already
/// exists to handle (see its own comment). Nothing downstream needs to
/// know or care that the input didn't start out as a PDF.
module QrLinkPdf.ImageToPdf

open System.IO
open iText.IO.Image
open iText.Kernel.Geom
open iText.Kernel.Pdf
open iText.Layout
open iText.Layout.Element

/// A phone photo of a printed page isn't *actually* any one true DPI - this
/// only has to be close enough that re-rasterizing the resulting page for
/// QR/OCR scanning doesn't badly over- or under-sample the source pixels
/// either direction. 150 is a reasonable middle ground for a typical photo.
let private assumedDpi = 150.0

/// Converts `imageBytes` to a single-page PDF, returned as bytes.
let convert (imageBytes: byte[]) : byte[] =
    let imageData = ImageDataFactory.Create(imageBytes)
    let widthPt = float32 (float (imageData.GetWidth()) * 72.0 / assumedDpi)
    let heightPt = float32 (float (imageData.GetHeight()) * 72.0 / assumedDpi)

    use output = new MemoryStream()
    use writer = new PdfWriter(output)
    writer.SetCloseStream(false) // We still need to read `output` after Close() below.
    use pdfDoc = new PdfDocument(writer)
    pdfDoc.SetDefaultPageSize(PageSize(widthPt, heightPt))
    use doc = new Document(pdfDoc)
    doc.SetMargins(0f, 0f, 0f, 0f)

    let img = Image(imageData)
    img.ScaleToFit(widthPt, heightPt) |> ignore
    img.SetFixedPosition(0f, 0f) |> ignore
    doc.Add(img) |> ignore

    doc.Close()
    output.ToArray()
