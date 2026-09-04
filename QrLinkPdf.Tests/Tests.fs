module QrLinkPdf.Tests.Tests

open System
open System.IO
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
    PdfQrLinker.scan options input

let private link (pdf: byte[]) =
    use input = new MemoryStream(pdf)
    use output = new MemoryStream()
    let links = PdfQrLinker.link options input output
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
    let found = PdfQrLinker.scan onlyExample input

    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/keep", found.Head.Uri)

[<Fact>]
let ``a UriFilter can rewrite the target`` () =
    let tracked =
        { options with
            UriFilter = fun text -> Some(text + "?utm_source=qr") }

    use input = new MemoryStream(single "https://example.com/page")
    let found = PdfQrLinker.scan tracked input
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
    // Scanning renders with WithAnnotations = false, so a second pass should
    // find the same codes and not compound the links.
    let once, _ = link (single "https://example.com/again")
    let twice, links = link once

    Assert.Equal(1, links.Length)
    Assert.Equal(2, (annotations twice).Length)

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

    let found = PdfQrLinker.scan options input
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
    let found = PdfQrLinker.scan ScanOptions.Default input
    Assert.Equal(1, found.Length)

[<Fact>]
let ``linkFile round-trips through the filesystem`` () =
    let directory = Path.Combine(Path.GetTempPath(), "qr-link-pdf-tests", Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory(directory) |> ignore

    try
        let input = Path.Combine(directory, "in.pdf")
        let output = Path.Combine(directory, "out.pdf")
        File.WriteAllBytes(input, single "https://example.com/on-disk")

        let links = PdfQrLinker.linkFile options input output

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
