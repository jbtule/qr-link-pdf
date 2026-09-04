/// Tests over roughed-up fixtures. The crisp fixtures in Tests.fs never
/// exercise the multi-scale pyramid in Scanner.fs, which is the whole reason
/// that code is there - these do.
///
/// These are inherently more sensitive to ZXing version changes than the crisp
/// ones, so they are kept few and deliberately not run at the margins of what
/// is detectable.
module QrLinkPdf.Tests.DegradedTests

open System.IO
open Xunit
open QrLinkPdf
open QrLinkPdf.Tests.TestPdfs

let private atDpi scales (pdf: byte[]) =
    use input = new MemoryStream(pdf)
    PdfQrLinker.scan { ScanOptions.Default with Dpi = 200; Scales = scales } input

/// One scan at full size - what the pyramid collapses to if someone decides
/// the extra levels aren't worth it.
let private singleScale = atDpi [ 1.0 ]

let private pyramid = atDpi ScanOptions.Default.Scales

let private fixture degradation =
    build [ placement "https://example.com/degraded" |> degraded degradation ]

[<Theory>]
[<InlineData(5.0)>]
[<InlineData(12.0)>]
[<InlineData(30.0)>]
let ``finds a code sitting askew on the page`` (degrees: float) =
    let found = pyramid (fixture (Rotated(float32 degrees)))
    Assert.Equal(1, found.Length)
    Assert.Equal("https://example.com/degraded", found.Head.Uri)

[<Theory>]
[<InlineData(0.3)>]
[<InlineData(0.2)>]
[<InlineData(0.12)>]
let ``finds a code that has been downsampled and stretched back`` (factor: float) =
    Assert.Equal(1, (pyramid (fixture (Resampled(float32 factor)))).Length)

[<Theory>]
[<InlineData(40)>]
[<InlineData(15)>]
[<InlineData(5)>]
let ``finds a code through JPEG artifacts`` (quality: int) =
    Assert.Equal(1, (pyramid (fixture (JpegArtifacts quality))).Length)

[<Fact>]
let ``finds a washed-out code only with the full pyramid`` () =
    // The point of the whole exercise: this code is invisible to a single
    // full-resolution pass and readable once shrunk. Delete the extra scan
    // levels from ScanOptions.Default and this test goes red.
    let pdf = fixture (Faded 0.7f)

    Assert.Empty(singleScale pdf)
    Assert.Equal(1, (pyramid pdf).Length)

[<Fact>]
let ``gives up on a code with almost no contrast left`` () =
    // Documents where the scanner's tolerance actually ends, so a future
    // change that quietly narrows it is visible.
    Assert.Empty(pyramid (fixture (Faded 0.85f)))

[<Theory>]
[<InlineData(200.0)>]
[<InlineData(120.0)>]
[<InlineData(70.0)>]
[<InlineData(60.0)>]
[<InlineData(45.0)>]
[<InlineData(30.0)>]
let ``reports one link per code, at any size`` (size: float) =
    // Regression test. Codes around 60pt used to come back twice: they decoded
    // only on ZXing's auto-rotated pass at the smallest pyramid level, and its
    // corner points are in the rotated image's coordinates, so the second
    // "find" landed transposed - sometimes off the page.
    let pdf = build [ placement "https://example.com/once" |> at (50f, 150f) |> sized (float32 size) ]
    let found = pyramid pdf

    Assert.Equal(1, found.Length)
    Assert.InRange(found.Head.Left, 40.0, 120.0)
    Assert.InRange(found.Head.Bottom, 140.0, 220.0)

[<Fact>]
let ``keeps degraded finds inside the page`` () =
    let pdf =
        build
            [ placement "https://example.com/a" |> sized 60f
              placement "https://example.com/b" |> at (330f, 500f) |> degraded (Rotated 20f) ]

    use doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(new MemoryStream(pdf)))
    let size = doc.GetPage(1).GetPageSize()

    let found = pyramid pdf
    Assert.Equal(2, found.Length)

    for link in found do
        Assert.InRange(link.Left, 0.0, float (size.GetWidth()))
        Assert.InRange(link.Right, 0.0, float (size.GetWidth()))
        Assert.InRange(link.Bottom, 0.0, float (size.GetHeight()))
        Assert.InRange(link.Top, 0.0, float (size.GetHeight()))
