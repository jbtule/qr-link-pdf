/// The stream-processing API: read a PDF from a stream, find the QR codes and
/// plain URL text on its pages, and optionally write out a copy with a
/// clickable link annotation over each one.
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

/// Rasterize every page and scan it for QR codes, translating each hit from
/// the bitmap's pixel space into PDF points for the page it was found on, and
/// splitting the hits into ones worth linking and ones that already have a
/// live hyperlink over them.
let private findQrLinks
    (options: ScanOptions)
    (pdfBytes: byte[])
    (doc: PdfDocument)
    (sizes: Map<int, float * float>)
    : QrLink list * QrLink list =
    // WithFormFill draws AcroForm field appearances. Barcode form fields - the
    // kind Acrobat generates from a calculation script - live there, and
    // without this they are simply absent from the raster and undetectable.
    // WithAnnotations stays off: it would draw the link annotations this tool
    // adds, so re-linking a file would find its own work.
    let renderOptions = RenderOptions(Dpi = options.Dpi, WithAnnotations = false, WithFormFill = true)

    let linked = ResizeArray<QrLink>()
    let alreadyLinked = ResizeArray<QrLink>()

    Conversion.ToImages(pdfBytes, options = renderOptions)
    |> Seq.indexed
    |> Seq.iter (fun (i, bitmap) ->
        use bitmap = bitmap
        let pageNumber = i + 1
        let pageSize = sizes.[pageNumber]
        let existing = ExistingLinks.rects (doc.GetPage(pageNumber))

        Scanner.findOnBitmap options bitmap
        |> List.iter (fun code ->
            match options.UriFilter code.Text with
            | None -> ()
            | Some uri ->
                let box = Geometry.pixelBoxToPoint pageSize (bitmap.Width, bitmap.Height) code.Box

                let link =
                    { PageNumber = pageNumber
                      Uri = uri
                      Left = box.Left
                      Bottom = box.Bottom
                      Width = box.Width
                      Height = box.Height }

                let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)

                if ExistingLinks.overlapsAny existing rect then
                    options.Trace(sprintf "  page %d: %s already linked, skipping" pageNumber uri)
                    alreadyLinked.Add link
                else
                    linked.Add link))

    List.ofSeq linked, List.ofSeq alreadyLinked

/// Find every linkable QR code and every linkable run of plain URL text on
/// every page of `doc`.
let private findInBytes (options: ScanOptions) (pdfBytes: byte[]) (doc: PdfDocument) : ScanResult =
    let sizes = pageSizes doc
    let qrLinked, qrAlreadyLinked = findQrLinks options pdfBytes doc sizes

    let textLinked, textAlreadyLinked =
        [ for pageNumber in 1 .. doc.GetNumberOfPages() -> TextLinker.findOnPage options pdfBytes doc pageNumber ]
        |> List.unzip

    { Links = qrLinked @ List.concat textLinked
      AlreadyLinked = qrAlreadyLinked @ List.concat textAlreadyLinked }

let private readAll (input: Stream) =
    match input with
    | :? MemoryStream as ms -> ms.ToArray()
    | _ ->
        use buffer = new MemoryStream()
        input.CopyTo(buffer)
        buffer.ToArray()

/// Add a clickable (invisible border) link annotation over `link` on its
/// page - the same treatment whether `link` came from a QR code or from
/// plain text.
let private addLinkAnnotation (doc: PdfDocument) (link: QrLink) =
    let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)
    let action = PdfAction.CreateURI(link.Uri)
    let annotation = PdfLinkAnnotation(rect).SetAction(action)
    // Zero-width, invisible border so the added link doesn't draw a visible
    // box over the QR code image.
    annotation.SetBorder(PdfArray([| 0; 0; 0 |])) |> ignore
    doc.GetPage(link.PageNumber).AddAnnotation(annotation) |> ignore

/// Find every linkable QR code and plain URL text run in the PDF read from
/// `input`, without modifying anything. The stream is read to the end but
/// left open.
let scan (options: ScanOptions) (input: Stream) : ScanResult =
    let bytes = readAll input
    use doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))
    findInBytes options bytes doc

/// Copy the PDF read from `input` to `output`, adding a clickable link
/// annotation over every QR code and plain URL text run whose payload passes
/// the URI filter and isn't already a live hyperlink, and return what was
/// linked and what was found already linked and left alone. Both streams are
/// left open.
let link (options: ScanOptions) (input: Stream) (output: Stream) : ScanResult =
    let bytes = readAll input

    use reader = new PdfReader(new MemoryStream(bytes))
    use writer = new PdfWriter(output)
    writer.SetCloseStream(false)
    use doc = new PdfDocument(reader, writer)

    let result = findInBytes options bytes doc

    for l in result.Links do
        addLinkAnnotation doc l

    result

/// File-path convenience wrapper around `link`.
let linkFile (options: ScanOptions) (inputPath: string) (outputPath: string) : ScanResult =
    use input = File.OpenRead(inputPath)
    use output = File.Create(outputPath)
    link options input output
