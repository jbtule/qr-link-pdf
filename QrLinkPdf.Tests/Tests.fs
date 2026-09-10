module QrLinkPdf.Tests.Tests

open System
open System.IO
open SkiaSharp
open Xunit
open iText.Kernel.Geom
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Annot
open QrLinkPdf
open QrLinkPdf.Tests.TestPdfs

/// The fixtures use large, clean codes, so one scan at a modest DPI finds them
/// all - no need to make the suite pay for the CLI's full pyramid.
let private options =
    { ScanOptions.Default with
        Dpi = 200
        Scales = [ 1.0 ] }

let private scan (pdf: byte[]) =
    use input = new MemoryStream(pdf)
    (PdfQrLinker.scan options input).Links

let private link (pdf: byte[]) =
    use input = new MemoryStream(pdf)
    use output = new MemoryStream()
    let links = (PdfQrLinker.link options input output).Links
    output.ToArray(), links

let private uris links =
    links |> List.map (fun l -> l.Uri) |> List.sort

/// Every link annotation in a PDF, as (page, uri, rect).
let private annotations (pdf: byte[]) =
    use doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)))

    [ for page in 1 .. doc.GetNumberOfPages() do
          for annotation in doc.GetPage(page).GetAnnotations() do
              match annotation with
              | :? PdfLinkAnnotation as annotation ->
                  let uri = annotation.GetAction().GetAsString(PdfName("URI")).ToUnicodeString()
                  yield page, uri, annotation.GetRectangle().ToRectangle()
              | _ -> () ]

// ---------------------------------------------------------------- finding

[<Fact>]
let ``finds a single code and reports its payload`` () =
    let found = scan (single "https://example.com/hello")
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/hello", found.Head.Uri)

[<Fact>]
let ``finds every code on a crowded page`` () =
    let payloads = [ for i in 1..6 -> sprintf "https://example.com/%d" i ]

    let placements =
        payloads
        |> List.mapi (fun i payload ->
            let column, row = i % 3, i / 3
            placement payload |> at (60f + float32 column * 170f, 90f + float32 row * 300f) |> sized 140f)

    let found = scan (build placements)
    Assert.Equal<string list>(List.sort payloads, uris found)

[<Fact>]
let ``reports the page each code was found on`` () =
    let pdf =
        buildPages
            PageSize.LETTER
            [ [ placement "https://example.com/one" ]
              []
              [ placement "https://example.com/three"
                placement "https://example.com/also-three" |> at (300f, 400f) ] ]

    let byPage =
        scan pdf
        |> List.map (fun l -> l.PageNumber, l.Uri)
        |> List.sort

    Assert.Equal<(int * string) list>(
        [ 1, "https://example.com/one"
          3, "https://example.com/also-three"
          3, "https://example.com/three" ],
        byPage
    )

[<Fact>]
let ``finds nothing in a PDF with no codes`` () =
    Assert.Empty(scan (build []))

[<Fact>]
let ``finds codes regardless of page size`` () =
    for size in [ PageSize.LETTER; PageSize.A4; PageSize.A5 ] do
        let found = scan (buildPages size [ [ placement "https://example.com/x" |> sized 120f ] ])
        Assert.Equal(1, found.Length)

[<Fact>]
let ``finds a code with its colours inverted`` () =
    // A QR code dropped into a brand-coloured box - light modules on a dark
    // background - rather than the usual dark-on-light.
    let pdf = build [ placement "https://example.com/inverted" |> degraded Inverted ]
    let found = scan pdf
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/inverted", found.Head.Uri)

[<Fact>]
let ``treats two codes with the same payload as separate finds`` () =
    let pdf =
        build
            [ placement "https://example.com/same"
              placement "https://example.com/same" |> at (330f, 500f) ]

    let found = scan pdf
    Assert.Equal(2, found.Length)
    Assert.All(found, fun l -> Assert.Equal("https://example.com/same", l.Uri))

// ---------------------------------------------------------------- filtering

[<Fact>]
let ``ignores payloads that aren't URLs`` () =
    let pdf =
        build
            [ placement "just some plain text"
              placement "BEGIN:VCARD\nFN:A Person\nEND:VCARD" |> at (330f, 500f) ]

    Assert.Empty(scan pdf)

[<Fact>]
let ``upgrades a bare www payload to https`` () =
    let found = scan (single "www.example.com/promo")
    Assert.Equal("https://www.example.com/promo", found.Head.Uri)

[<Theory>]
[<InlineData("https://example.com/a")>]
[<InlineData("http://example.com/b")>]
[<InlineData("mailto:someone@example.com")>]
[<InlineData("tel:+15555550123")>]
let ``links any absolute URI`` (payload: string) =
    let found = scan (single payload)
    Assert.Equal(1, found.Length)
    Assert.Equal(payload, found.Head.Uri)

[<Fact>]
let ``honours a custom UriFilter`` () =
    let pdf =
        build
            [ placement "https://example.com/keep"
              placement "https://elsewhere.test/drop" |> at (330f, 500f) ]

    let onlyExample =
        { options with
            UriFilter = fun text -> if text.StartsWith "https://example.com/" then Some text else None }

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan onlyExample input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/keep", found.Head.Uri)

[<Fact>]
let ``a UriFilter can rewrite the target`` () =
    let tracked =
        { options with
            UriFilter = fun text -> Some(text + "?utm_source=qr") }

    use input = new MemoryStream(single "https://example.com/page")
    let found = (PdfQrLinker.scan tracked input).Links
    Assert.Equal("https://example.com/page?utm_source=qr", found.Head.Uri)

// ---------------------------------------------------------------- geometry

[<Fact>]
let ``reports the code's position in PDF points`` () =
    // Deliberately off-centre and nearer the top, so a flipped or transposed
    // axis can't accidentally land in the right place.
    let expected = placement "https://example.com/where" |> at (140f, 560f) |> sized 150f

    let found = (scan (build [ expected ])).Head

    // The detected box is the symbol plus padding, and the image carries a
    // quiet zone, so compare centres rather than edges.
    let centreX = found.Left + found.Width / 2.0
    let centreY = found.Bottom + found.Height / 2.0
    let expectedCentre = float expected.Left + float expected.Size / 2.0

    Assert.InRange(centreX, expectedCentre - 12.0, expectedCentre + 12.0)
    Assert.InRange(centreY, float expected.Bottom + float expected.Size / 2.0 - 12.0, float expected.Bottom + float expected.Size / 2.0 + 12.0)

    // And it should be roughly the size of the code we drew.
    Assert.InRange(found.Width, float expected.Size * 0.7, float expected.Size * 1.4)
    Assert.InRange(found.Height, float expected.Size * 0.7, float expected.Size * 1.4)

[<Fact>]
let ``distinguishes top from bottom of the page`` () =
    // The y-flip between image space and PDF space is the easiest thing to get
    // backwards, and a symmetric layout would hide it.
    let pdf =
        build
            [ placement "https://example.com/low" |> at (80f, 60f) |> sized 120f
              placement "https://example.com/high" |> at (80f, 600f) |> sized 120f ]

    let found = scan pdf
    let low = found |> List.find (fun l -> l.Uri.EndsWith "/low")
    let high = found |> List.find (fun l -> l.Uri.EndsWith "/high")

    Assert.True(high.Bottom > low.Bottom, "the code drawn higher up should have the larger Y")
    Assert.InRange(low.Bottom, 40.0, 110.0)
    Assert.InRange(high.Bottom, 580.0, 650.0)

[<Fact>]
let ``keeps every found code inside its page`` () =
    let pdf = build [ placement "https://example.com/a"; placement "https://example.com/b" |> at (380f, 620f) ]

    use doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)))
    let size = doc.GetPage(1).GetPageSize()

    for l in scan pdf do
        Assert.InRange(l.Left, 0.0, float (size.GetWidth()))
        Assert.InRange(l.Bottom, 0.0, float (size.GetHeight()))
        Assert.True(l.Right <= float (size.GetWidth()), "right edge inside the page")
        Assert.True(l.Top <= float (size.GetHeight()), "top edge inside the page")

// ---------------------------------------------------------------- annotating

[<Fact>]
let ``writes one URI annotation per code found`` () =
    let pdf = build [ placement "https://example.com/one"; placement "https://example.com/two" |> at (330f, 500f) ]

    let output, links = link pdf
    let written = annotations output

    Assert.Equal(2, links.Length)
    Assert.Equal<string list>(uris links, written |> List.map (fun (_, uri, _) -> uri) |> List.sort)

[<Fact>]
let ``puts each annotation where the code was found`` () =
    let output, links = link (single "https://example.com/spot")
    let _, _, rect = (annotations output).Head
    let found = links.Head

    Assert.Equal(found.Left, float (rect.GetLeft()), 1)
    Assert.Equal(found.Bottom, float (rect.GetBottom()), 1)
    Assert.Equal(found.Width, float (rect.GetWidth()), 1)
    Assert.Equal(found.Height, float (rect.GetHeight()), 1)

[<Fact>]
let ``gives annotations an invisible border`` () =
    // Otherwise the reader draws a box over the QR code.
    let output, _ = link (single "https://example.com/border")

    use doc = new PdfDocument(new PdfReader(new MemoryStream(output)))

    let borders =
        [ for annotation in doc.GetPage(1).GetAnnotations() do
              match annotation with
              | :? PdfLinkAnnotation as annotation ->
                  match annotation.GetBorder() with
                  | null -> ()
                  | border -> yield [ for i in 0 .. border.Size() - 1 -> border.GetAsNumber(i).IntValue() ]
              | _ -> () ]

    Assert.Equal<int list list>([ [ 0; 0; 0 ] ], borders)

[<Fact>]
let ``annotates the correct page`` () =
    let pdf =
        buildPages PageSize.LETTER [ []; [ placement "https://example.com/page-two" ] ]

    let output, _ = link pdf
    Assert.Equal<(int * string) list>([ 2, "https://example.com/page-two" ], annotations output |> List.map (fun (p, u, _) -> p, u))

[<Fact>]
let ``leaves a PDF without codes structurally intact`` () =
    let pdf = build []
    let output, links = link pdf

    Assert.Empty(links)
    Assert.Empty(annotations output)

    use before = new PdfDocument(new PdfReader(new MemoryStream(pdf)))
    use after = new PdfDocument(new PdfReader(new MemoryStream(output)))
    Assert.Equal(before.GetNumberOfPages(), after.GetNumberOfPages())

[<Fact>]
let ``preserves page count and size`` () =
    let pdf = buildPages PageSize.A4 [ [ placement "https://example.com/1" ]; []; [ placement "https://example.com/3" ] ]
    let output, _ = link pdf

    use doc = new PdfDocument(new PdfReader(new MemoryStream(output)))
    Assert.Equal(3, doc.GetNumberOfPages())
    Assert.Equal(float (PageSize.A4.GetWidth()), float (doc.GetPage(1).GetPageSize().GetWidth()), 1)

[<Fact>]
let ``the result can be linked again without duplicating annotations`` () =
    // A second pass should recognize the code it already linked and leave it
    // alone, rather than adding a duplicate annotation over it.
    let once, _ = link (single "https://example.com/again")

    use input = new MemoryStream(once)
    use output = new MemoryStream()
    let result = PdfQrLinker.link options input output
    let twice = output.ToArray()

    Assert.Empty(result.Links)
    Assert.Equal(1, result.AlreadyLinked.Length)
    Assert.Equal("https://example.com/again", result.AlreadyLinked.Head.Uri)
    Assert.Equal(1, (annotations twice).Length)

[<Fact>]
let ``does not re-link a QR code that already has a link annotation`` () =
    let pdf = buildWithExistingLink [ placement "https://example.com/already-linked-qr" ] "https://example.com/already-linked-qr"

    use input = new MemoryStream(pdf)
    let result = PdfQrLinker.scan options input

    Assert.Empty(result.Links)
    Assert.Equal(1, result.AlreadyLinked.Length)
    Assert.Equal("https://example.com/already-linked-qr", result.AlreadyLinked.Head.Uri)

[<Fact>]
let ``reports an already-linked QR code separately from newly linked ones`` () =
    let placements =
        [ placement "https://example.com/fresh"
          placement "https://example.com/stale" |> at (330f, 500f) ]

    let pdf = buildWithExistingLink placements "https://example.com/stale"

    use input = new MemoryStream(pdf)
    let result = PdfQrLinker.scan options input

    Assert.Equal(1, result.Links.Length)
    Assert.Equal("https://example.com/fresh", result.Links.Head.Uri)
    Assert.Equal(1, result.AlreadyLinked.Length)
    Assert.Equal("https://example.com/stale", result.AlreadyLinked.Head.Uri)

// ---------------------------------------------------------------- plumbing

[<Fact>]
let ``leaves the caller's streams open`` () =
    use input = new MemoryStream(single "https://example.com/streams")
    use output = new MemoryStream()

    PdfQrLinker.link options input output |> ignore

    Assert.True(input.CanRead, "input should still be open")
    Assert.True(output.CanWrite, "output should still be open")
    Assert.True(output.Length > 0L)

[<Fact>]
let ``reads a forward-only stream`` () =
    // What a browser upload or a pipe looks like: no seeking, no known length.
    let bytes = single "https://example.com/forward"

    use inner = new MemoryStream(bytes)
    use input = new BufferedStream(inner, 128)

    let found = (PdfQrLinker.scan options input).Links
    Assert.Equal(1, found.Length)

[<Fact>]
let ``sends diagnostics to Trace`` () =
    let lines = ResizeArray<string>()

    use input = new MemoryStream(single "https://example.com/trace")
    PdfQrLinker.scan { options with Trace = lines.Add } input |> ignore

    Assert.NotEmpty(lines)
    Assert.Contains(lines, fun line -> line.Contains "bitmap")

[<Fact>]
let ``says nothing when Trace is left at its default`` () =
    // The default is `ignore`; this just pins that scanning is silent unless
    // asked, since the library has no business writing to the console.
    use input = new MemoryStream(single "https://example.com/quiet")
    let found = (PdfQrLinker.scan ScanOptions.Default input).Links
    Assert.Equal(1, found.Length)

[<Fact>]
let ``linkFile round-trips through the filesystem`` () =
    let directory = Path.Combine(Path.GetTempPath(), "qr-link-pdf-tests", Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory(directory) |> ignore

    try
        let input = Path.Combine(directory, "in.pdf")
        let output = Path.Combine(directory, "out.pdf")
        File.WriteAllBytes(input, single "https://example.com/on-disk")

        let links = (PdfQrLinker.linkFile options input output).Links

        Assert.Equal(1, links.Length)
        Assert.True(File.Exists output)
        Assert.Equal<string list>([ "https://example.com/on-disk" ], annotations (File.ReadAllBytes output) |> List.map (fun (_, u, _) -> u))
    finally
        Directory.Delete(directory, true)

[<Fact>]
let ``finds a code drawn by an AcroForm field`` () =
    // Acrobat's barcode fields put the code in the field's appearance stream,
    // not the page content, so it exists only when form rendering is on. A real
    // document of these scanned as blank until PdfQrLinker enabled it.
    let pdf = buildFormField "https://example.com/form-field" (72f, 500f) 160f

    let found = scan pdf
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/form-field", found.Head.Uri)

[<Fact>]
let ``annotates a code drawn by an AcroForm field`` () =
    let output, links = link (buildFormField "https://example.com/form-field" (200f, 300f) 150f)

    Assert.Equal(1, links.Length)
    Assert.Equal<string list>([ "https://example.com/form-field" ], annotations output |> List.map (fun (_, u, _) -> u))

// ------------------------------------------------------------ text-linking

[<Fact>]
let ``finds a plain-text URL mid-sentence`` () =
    let pdf = textParagraph "Visit https://example.com/hello today." (72f, 700f) 400f

    let found = scan pdf
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/hello", found.Head.Uri)

[<Fact>]
let ``finds a URL split across two text runs on the same line`` () =
    let text = "See https://example.com/split-here for details."
    let pdf = textParagraphSplit text (text.IndexOf "split") (72f, 700f) 400f

    let found = scan pdf
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/split-here", found.Head.Uri)

[<Fact>]
let ``trims trailing punctuation from a URL in prose`` () =
    let pdf = textParagraph "See (https://example.com/hello), thanks." (72f, 700f) 400f

    let found = scan pdf
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/hello", found.Head.Uri)

[<Fact>]
let ``does not re-link text that already has a link annotation`` () =
    let pdf =
        textWithExistingLink "Visit https://example.com/already-linked today." "https://example.com/already-linked" (72f, 700f) 400f

    Assert.Empty(scan pdf)

[<Fact>]
let ``ignores plain text that isn't URL-shaped`` () =
    let pdf = textParagraph "Just an ordinary sentence with no links in it." (72f, 700f) 400f
    Assert.Empty(scan pdf)

[<Fact>]
let ``merges QR and text-derived links into one scan result`` () =
    let pdf =
        buildQrAndText
            "https://example.com/qr"
            "Also visit https://example.com/text for more."
            (72f, 72f)
            160f
            (72f, 500f)
            400f

    let found = uris (scan pdf)
    Assert.Equal<string list>([ "https://example.com/qr"; "https://example.com/text" ], found)

[<Fact>]
let ``honours a custom UriFilter for text-detected URLs too`` () =
    let pdf = textParagraph "See https://elsewhere.test/drop for details." (72f, 700f) 400f

    let onlyExample =
        { options with
            UriFilter = fun text -> if text.StartsWith "https://example.com/" then Some text else None }

    use input = new MemoryStream(pdf)
    Assert.Empty((PdfQrLinker.scan onlyExample input).Links)

[<Fact>]
let ``a text-derived link gets the same invisible-border annotation as a QR one`` () =
    let output, links = link (textParagraph "Visit https://example.com/text-link now." (72f, 700f) 400f)

    Assert.Equal(1, links.Length)
    let _, uri, _ = (annotations output).Head
    Assert.Equal("https://example.com/text-link", uri)

    use doc = new PdfDocument(new PdfReader(new MemoryStream(output)))

    let border =
        [ for annotation in doc.GetPage(1).GetAnnotations() do
              match annotation with
              | :? PdfLinkAnnotation as annotation ->
                  yield [ for i in 0 .. annotation.GetBorder().Size() - 1 -> annotation.GetBorder().GetAsNumber(i).IntValue() ]
              | _ -> () ]

    Assert.Equal<int list list>([ [ 0; 0; 0 ] ], border)

// ------------------------------------------------------ bare-domain matching

[<Fact>]
let ``ignores a bare domain by default`` () =
    let pdf = textParagraph "Get the app: qrco.de/trails-end" (72f, 700f) 400f
    Assert.Empty(scan pdf)

[<Fact>]
let ``finds a bare domain when opted in`` () =
    let pdf = textParagraph "Get the app: qrco.de/trails-end" (72f, 700f) 400f
    let bareDomains = { options with MatchBareDomains = true }

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan bareDomains input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://qrco.de/trails-end", found.Head.Uri)

[<Fact>]
let ``does not mistake a decimal figure with a slash for a domain`` () =
    let pdf = textParagraph "See section 3.14/2 for the formula." (72f, 700f) 400f
    let bareDomains = { options with MatchBareDomains = true }

    use input = new MemoryStream(pdf)
    Assert.Empty((PdfQrLinker.scan bareDomains input).Links)

[<Fact>]
let ``does not mistake a version number with a slash for a domain`` () =
    let pdf = textParagraph "Requires v1.2/beta or later." (72f, 700f) 400f
    let bareDomains = { options with MatchBareDomains = true }

    use input = new MemoryStream(pdf)
    Assert.Empty((PdfQrLinker.scan bareDomains input).Links)

[<Fact>]
let ``does not double-count a scheme URL as a bare domain too`` () =
    let pdf = textParagraph "Visit https://qrco.de/trails-end today." (72f, 700f) 400f
    let bareDomains = { options with MatchBareDomains = true }

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan bareDomains input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://qrco.de/trails-end", found.Head.Uri)

[<Fact>]
let ``honours a custom UriFilter for bare domains too`` () =
    let pdf = textParagraph "Get the app: qrco.de/trails-end" (72f, 700f) 400f

    let onlyExample =
        { options with
            MatchBareDomains = true
            UriFilter = fun text -> if text.StartsWith "https://example.com/" then Some text else None }

    use input = new MemoryStream(pdf)
    Assert.Empty((PdfQrLinker.scan onlyExample input).Links)

// ------------------------------------------------------------- OCR fallback

[<Fact>]
let ``does nothing on a text-free page when no OcrEngine is set`` () =
    // ScanOptions.OcrEngine defaults to None - this pins that a page with
    // zero extractable text just comes back empty, not an error.
    Assert.Empty(scan (blankPage PageSize.LETTER))

[<Fact>]
let ``finds a URL via OCR on a page with no extractable text`` () =
    let fakeWord: OcrWord =
        { Text = "https://example.com/ocr"
          Box = SKRectI(100, 100, 500, 140) }

    let withOcr =
        { options with
            OcrEngine = Some(fun _ -> [ fakeWord ]) }

    use input = new MemoryStream(blankPage PageSize.LETTER)
    let found = (PdfQrLinker.scan withOcr input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/ocr", found.Head.Uri)
    Assert.InRange(found.Head.Left, 0.0, 612.0)
    Assert.InRange(found.Head.Bottom, 0.0, 792.0)

[<Fact>]
let ``still runs OCR on a page that already has some real text`` () =
    // A page can have a little real text (page numbers, a heading) while
    // its body copy is flattened to vector outlines elsewhere on the same
    // page - a "skip OCR if anything was found" trigger would miss that
    // real-world case, so OCR always runs when an engine is supplied.
    let withOcr =
        { options with
            OcrEngine = Some(fun _ -> [ { Text = "https://example.com/ocr-found-it"; Box = SKRectI(100, 100, 500, 140) } ]) }

    let pdf = textParagraph "Just an ordinary sentence with no links in it." (72f, 700f) 400f

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan withOcr input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/ocr-found-it", found.Head.Uri)

[<Fact>]
let ``skips OCR'd text that already has a link annotation`` () =
    // The existing annotation covers the whole page, so it overlaps
    // whatever pixel box the fake engine reports regardless of the exact
    // pixel-to-point conversion.
    let pdf = blankPageWithExistingLink PageSize.LETTER "https://example.com/already-linked" (Rectangle(0f, 0f, 612f, 792f))

    let withOcr =
        { options with
            OcrEngine = Some(fun _ -> [ { Text = "https://example.com/already-linked"; Box = SKRectI(100, 100, 500, 140) } ]) }

    use input = new MemoryStream(pdf)
    Assert.Empty((PdfQrLinker.scan withOcr input).Links)

[<Fact>]
let ``an OCR-derived link gets annotated like any other`` () =
    let withOcr =
        { options with
            OcrEngine = Some(fun _ -> [ { Text = "https://example.com/ocr-linked"; Box = SKRectI(100, 100, 500, 140) } ]) }

    use input = new MemoryStream(blankPage PageSize.LETTER)
    use output = new MemoryStream()
    let links = (PdfQrLinker.link withOcr input output).Links

    Assert.Equal(1, links.Length)
    Assert.Equal<string list>([ "https://example.com/ocr-linked" ], annotations (output.ToArray()) |> List.map (fun (_, u, _) -> u))

[<Fact>]
let ``does not duplicate a URL that OCR reports right next to where real text found it`` () =
    // Real text and OCR can disagree on a word's exact vertical extent for
    // the same visible line - font-metric ascent/descent vs OCR's tight ink
    // bounding box - closely enough to land with zero literal rectangle
    // overlap. Confirmed against a real document, where this showed up as
    // the same URL linked twice. Learn where real-text extraction actually
    // found it, then have a fake OCR engine report the same URL shifted
    // down by less than one line height - the near-miss shape that
    // triggered it - and confirm only one link survives.
    let pdf = textParagraph "Visit https://example.com/both-sources today." (72f, 700f) 400f

    use probeInput = new MemoryStream(pdf)
    let real = (PdfQrLinker.scan options probeInput).Links.Head

    let fakeEngine (bitmap: SKBitmap) : OcrWord list =
        let pageHeight = 792.0
        let toPixelX (x: float) = int (x * float bitmap.Width / 612.0)
        let toPixelY (y: float) = int ((pageHeight - y) * float bitmap.Height / pageHeight)
        let shift = real.Height * 0.5

        [ { Text = real.Uri
            Box =
              SKRectI(
                  toPixelX real.Left,
                  toPixelY (real.Top - shift),
                  toPixelX real.Right,
                  toPixelY (real.Bottom - shift)
              ) } ]

    let withOcr = { options with OcrEngine = Some fakeEngine }

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan withOcr input).Links

    Assert.Equal(1, found.Length)

[<Fact>]
let ``prefers real text over an OCR misread at the same spot`` () =
    // Regression test for a real-world false link: a flyer's printed URL
    // ("www.wlf.la.gov/page/cwd") got real-text-extracted correctly, but
    // OCR - run unconditionally over the same page, see findOnPage's own
    // doc comment - misread "wlf" as "wif" at the exact same spot, adding a
    // second, wrong link nobody wanted (dedupeBySpot's own URI-equality
    // check didn't catch it, because the misread text means the two
    // candidates' URIs genuinely differ, not just their rects). Real text
    // is authoritative wherever it exists at all - OCR exists to cover
    // *pages that have none* - so a same-spot OCR candidate should lose to
    // it regardless of what OCR thinks the text says.
    let pdf = textParagraph "Visit https://example.com/wlf today." (72f, 700f) 400f

    use probeInput = new MemoryStream(pdf)
    let real = (PdfQrLinker.scan options probeInput).Links.Head

    let fakeEngine (bitmap: SKBitmap) : OcrWord list =
        let pageHeight = 792.0
        let toPixelX (x: float) = int (x * float bitmap.Width / 612.0)
        let toPixelY (y: float) = int ((pageHeight - y) * float bitmap.Height / pageHeight)

        [ { Text = "https://example.com/wif" // OCR's misread of the same spot
            Box =
              SKRectI(
                  toPixelX real.Left,
                  toPixelY real.Top,
                  toPixelX real.Right,
                  toPixelY real.Bottom
              ) } ]

    let withOcr = { options with OcrEngine = Some fakeEngine }

    use input = new MemoryStream(pdf)
    let found = (PdfQrLinker.scan withOcr input).Links

    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/wlf", found.Head.Uri)

[<Fact>]
let ``does not add a duplicate annotation when OCR's near-miss escapes the overlap check that catches real text`` () =
    // Regression test for a real-world duplicate: a URL already linked, with
    // real text extraction correctly recognizing the overlap (existing rect
    // vs its own precise rect clears the >50% threshold) while OCR's
    // shifted-by-half-a-line-height rect for the *same* URL just barely
    // misses that same threshold against the existing annotation. Partitioning
    // each source's candidate independently against `existing` used to let
    // OCR's near-miss slip into `Links` and get linked a second time, right
    // on top of the live annotation, even though the real-text candidate for
    // the identical URL correctly landed in `AlreadyLinked`.
    let uri = "https://example.com/already-linked-both-sources"
    let pdf = textWithExistingLink (sprintf "Visit %s today." uri) uri (72f, 700f) 400f

    use probeInput = new MemoryStream(pdf)
    let real = (PdfQrLinker.scan options probeInput).AlreadyLinked.Head

    let fakeEngine (bitmap: SKBitmap) : OcrWord list =
        let pageHeight = 792.0
        let toPixelX (x: float) = int (x * float bitmap.Width / 612.0)
        let toPixelY (y: float) = int ((pageHeight - y) * float bitmap.Height / pageHeight)
        let shift = real.Height * 0.5

        [ { Text = real.Uri
            Box =
              SKRectI(
                  toPixelX real.Left,
                  toPixelY (real.Top - shift),
                  toPixelX real.Right,
                  toPixelY (real.Bottom - shift)
              ) } ]

    let withOcr = { options with OcrEngine = Some fakeEngine }

    use scanInput = new MemoryStream(pdf)
    let result = PdfQrLinker.scan withOcr scanInput

    Assert.Empty(result.Links)
    Assert.Equal(1, result.AlreadyLinked.Length)

    use linkInput = new MemoryStream(pdf)
    use output = new MemoryStream()
    let linked = PdfQrLinker.link withOcr linkInput output

    Assert.Empty(linked.Links)
    Assert.Equal(1, (annotations (output.ToArray())).Length)
