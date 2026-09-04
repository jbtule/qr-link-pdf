/// The stream-processing API: read a PDF from a stream, find the QR codes on
/// its pages, and optionally write out a copy with a clickable link
/// annotation over each one.
module QrLinkPdf.PdfQrLinker

open System.IO
open PDFtoImage
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Action
open iText.Kernel.Pdf.Annot
open iText.Kernel.Geom

/// Page sizes in points, keyed by 1-based page number.
let private pageSizes (doc: PdfDocument) =
    [ for i in 1 .. doc.GetNumberOfPages() ->
          let size = doc.GetPage(i).GetPageSize()
          i, (float (size.GetWidth()), float (size.GetHeight())) ]
    |> Map.ofList

/// Rasterize every page and scan it, translating each hit from the bitmap's
/// pixel space into PDF points for the page it was found on.
let private findInBytes (options: ScanOptions) (pdfBytes: byte[]) (sizes: Map<int, float * float>) =
    // WithFormFill draws AcroForm field appearances. Barcode form fields - the
    // kind Acrobat generates from a calculation script - live there, and
    // without this they are simply absent from the raster and undetectable.
    // WithAnnotations stays off: it would draw the link annotations this tool
    // adds, so re-linking a file would find its own work.
    let renderOptions = RenderOptions(Dpi = options.Dpi, WithAnnotations = false, WithFormFill = true)

    Conversion.ToImages(pdfBytes, options = renderOptions)
    |> Seq.indexed
    |> Seq.collect (fun (i, bitmap) ->
        use bitmap = bitmap
        let pageNumber = i + 1
        let pageWidthPt, pageHeightPt = sizes.[pageNumber]
        // Pixels-per-point on this render, so we can map bitmap coordinates
        // back to the PDF's own coordinate space.
        let scaleX = pageWidthPt / float bitmap.Width
        let scaleY = pageHeightPt / float bitmap.Height

        Scanner.findOnBitmap options bitmap
        |> List.choose (fun code ->
            match options.UriFilter code.Text with
            | None -> None
            | Some uri ->
                let left = float code.Box.Left * scaleX
                let right = float code.Box.Right * scaleX
                // Image Y grows downward from the top; PDF Y grows upward
                // from the bottom, so flip here.
                let top = pageHeightPt - float code.Box.Top * scaleY
                let bottom = pageHeightPt - float code.Box.Bottom * scaleY

                Some
                    { PageNumber = pageNumber
                      Uri = uri
                      Left = left
                      Bottom = bottom
                      Width = right - left
                      Height = top - bottom }))
    |> List.ofSeq

let private readAll (input: Stream) =
    match input with
    | :? MemoryStream as ms -> ms.ToArray()
    | _ ->
        use buffer = new MemoryStream()
        input.CopyTo(buffer)
        buffer.ToArray()

/// Add a clickable (invisible border) link annotation over `link` on its page.
let private addLinkAnnotation (doc: PdfDocument) (link: QrLink) =
    let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)
    let action = PdfAction.CreateURI(link.Uri)
    let annotation = PdfLinkAnnotation(rect).SetAction(action)
    // Zero-width, invisible border so the added link doesn't draw a visible
    // box over the QR code image.
    annotation.SetBorder(PdfArray([| 0; 0; 0 |])) |> ignore
    doc.GetPage(link.PageNumber).AddAnnotation(annotation) |> ignore

/// Find every linkable QR code in the PDF read from `input`, without
/// modifying anything. The stream is read to the end but left open.
let scan (options: ScanOptions) (input: Stream) : QrLink list =
    let bytes = readAll input
    use doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))
    findInBytes options bytes (pageSizes doc)

/// Copy the PDF read from `input` to `output`, adding a clickable link
/// annotation over every QR code whose payload passes the URI filter, and
/// return the links that were added. Both streams are left open.
let link (options: ScanOptions) (input: Stream) (output: Stream) : QrLink list =
    let bytes = readAll input

    use reader = new PdfReader(new MemoryStream(bytes))
    use writer = new PdfWriter(output)
    writer.SetCloseStream(false)
    use doc = new PdfDocument(reader, writer)

    let links = findInBytes options bytes (pageSizes doc)

    for l in links do
        addLinkAnnotation doc l

    links

/// File-path convenience wrapper around `link`.
let linkFile (options: ScanOptions) (inputPath: string) (outputPath: string) : QrLink list =
    use input = File.OpenRead(inputPath)
    use output = File.Create(outputPath)
    link options input output
