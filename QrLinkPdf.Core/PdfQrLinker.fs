/// The stream-processing API: read a PDF from a stream, find the QR codes and
/// plain URL text on its pages, and optionally write out a copy with a
/// clickable link annotation over each one.
module QrLinkPdf.PdfQrLinker

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open PDFtoImage
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Action
open iText.Kernel.Pdf.Annot
open iText.Kernel.Geom

/// "1.23s", for the phase-timing Trace lines below.
let private seconds (stopwatch: Stopwatch) = sprintf "%.2fs" stopwatch.Elapsed.TotalSeconds

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
///
/// Pages are rendered one at a time (rather than through the lazy
/// `Conversion.ToImages` sequence) so `progress` can be awaited *before*
/// each page's raster + decode, not after it - the sequence would render
/// page N as a side effect of asking for it, before any report could go
/// out. Re-opening the document per page is cheap next to a 300 DPI
/// rasterization.
let private findQrLinks
    (options: ScanOptions)
    (progress: ScanProgress -> Task)
    (pdfBytes: byte[])
    (doc: PdfDocument)
    (sizes: Map<int, float * float>)
    : Task<QrLink list * QrLink list> =
    task {
        // WithFormFill draws AcroForm field appearances. Barcode form fields
        // - the kind Acrobat generates from a calculation script - live
        // there, and without this they are simply absent from the raster
        // and undetectable. WithAnnotations stays off: it would draw the
        // link annotations this tool adds, so re-linking a file would find
        // its own work.
        let renderOptions = RenderOptions(Dpi = options.Dpi, WithAnnotations = false, WithFormFill = true)

        let linked = ResizeArray<QrLink>()
        let alreadyLinked = ResizeArray<QrLink>()
        let pageCount = doc.GetNumberOfPages()

        for pageNumber in 1..pageCount do
            do! progress (ScanningQr(pageNumber, pageCount))
            use bitmap = Conversion.ToImage(pdfBytes, System.Index(pageNumber - 1), options = renderOptions)
            let pageSize = sizes.[pageNumber]
            let existing = ExistingLinks.rects (doc.GetPage(pageNumber))

            for code in Scanner.findOnBitmap options bitmap do
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
                        linked.Add link

        return List.ofSeq linked, List.ofSeq alreadyLinked
    }

/// Find every linkable QR code and every linkable run of plain URL text on
/// every page of `doc`. Each phase's own wall time goes through Trace right
/// after it finishes - `scan`/`link` add the overall total once this
/// returns, so a run's log always ends with a full phase-by-phase
/// breakdown, not just a "found N links" count.
let private findInBytes
    (options: ScanOptions)
    (progress: ScanProgress -> Task)
    (pdfBytes: byte[])
    (doc: PdfDocument)
    : Task<ScanResult> =
    task {
        let sizes = pageSizes doc
        let pageCount = doc.GetNumberOfPages()

        let qrStopwatch = Stopwatch.StartNew()
        let! qrLinked, qrAlreadyLinked = findQrLinks options progress pdfBytes doc sizes
        options.Trace(sprintf "QR scan: %s" (seconds qrStopwatch))

        let textStopwatch = Stopwatch.StartNew()
        let textLinked = ResizeArray<QrLink list>()
        let textAlreadyLinked = ResizeArray<QrLink list>()

        for pageNumber in 1..pageCount do
            do! progress (ScanningText(pageNumber, pageCount))
            let linked, alreadyLinked = TextLinker.findOnPage options pdfBytes doc pageNumber
            textLinked.Add linked
            textAlreadyLinked.Add alreadyLinked

        // Includes OCR time when an OcrEngine is set - TextLinker.findOnPage
        // runs it inline per page, not as a separate phase, so there's no
        // narrower boundary to time it at without threading a second
        // stopwatch through every OCR call site.
        options.Trace(sprintf "Text/OCR scan: %s" (seconds textStopwatch))

        return
            { Links = qrLinked @ List.concat textLinked
              AlreadyLinked = qrAlreadyLinked @ List.concat textAlreadyLinked }
    }

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

/// The progress callback `scan`/`link` use: report nothing, wait for nothing,
/// so the whole pipeline runs synchronously on the calling thread exactly
/// as it did before progress reporting existed.
let private noProgress (_: ScanProgress) : Task = Task.CompletedTask

/// `scan`, with a progress callback awaited before each page's work - see
/// `ScanProgress`. Returning an already-completed Task keeps the whole scan
/// synchronous; returning something that actually waits (a browser host
/// waiting for a repaint) hands the thread back between pages.
let scanAsync (options: ScanOptions) (progress: ScanProgress -> Task) (input: Stream) : Task<ScanResult> =
    task {
        let totalStopwatch = Stopwatch.StartNew()
        let bytes = readAll input
        use doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))
        let! result = findInBytes options progress bytes doc
        options.Trace(sprintf "Total: %s" (seconds totalStopwatch))
        return result
    }

/// Find every linkable QR code and plain URL text run in the PDF read from
/// `input`, without modifying anything. The stream is read to the end but
/// left open.
let scan (options: ScanOptions) (input: Stream) : ScanResult =
    // Never actually blocks: noProgress completes synchronously, so the
    // task has already finished by the time this asks for its result.
    (scanAsync options noProgress input).GetAwaiter().GetResult()

/// `link`, with a progress callback awaited before each page's work and
/// before the final write-out - see `scanAsync`.
let linkAsync
    (options: ScanOptions)
    (progress: ScanProgress -> Task)
    (input: Stream)
    (output: Stream)
    : Task<ScanResult> =
    task {
        let totalStopwatch = Stopwatch.StartNew()
        let bytes = readAll input

        use reader = new PdfReader(new MemoryStream(bytes))
        use writer = new PdfWriter(output)
        writer.SetCloseStream(false)
        use doc = new PdfDocument(reader, writer)

        let! result = findInBytes options progress bytes doc

        do! progress Writing

        for l in result.Links do
            addLinkAnnotation doc l

        // Explicit Close (idempotent - `use` above still runs its Dispose as
        // a safety net) rather than leaving it to disposal: PdfDocument
        // writes lazily, so closing here rather than after Total is traced
        // is what makes Total include the actual write-out, not just the
        // scan + in-memory annotation step.
        doc.Close()
        options.Trace(sprintf "Total: %s" (seconds totalStopwatch))
        return result
    }

/// Copy the PDF read from `input` to `output`, adding a clickable link
/// annotation over every QR code and plain URL text run whose payload passes
/// the URI filter and isn't already a live hyperlink, and return what was
/// linked and what was found already linked and left alone. Both streams are
/// left open.
let link (options: ScanOptions) (input: Stream) (output: Stream) : ScanResult =
    (linkAsync options noProgress input output).GetAwaiter().GetResult()

/// File-path convenience wrapper around `link`.
let linkFile (options: ScanOptions) (inputPath: string) (outputPath: string) : ScanResult =
    use input = File.OpenRead(inputPath)
    use output = File.Create(outputPath)
    link options input output
