/// Every other OCR test in this suite (see Tests.fs's own "OCR fallback"
/// section) injects a fake `OcrEngine` - the right choice there, since those
/// tests are really about PdfQrLinker's linking/dedup logic, not about
/// whether Tesseract itself works. Nothing in the suite ever constructed a
/// real `TesseractEngine` until this file: these tests exist specifically to
/// catch packaging/native-loading regressions (wrong library name, dropped
/// runtime asset, the kind of bug jbtule/tesseract-nuget-platforms has
/// actually shipped before) that a fake engine can never see.
///
/// Needs `tessdata/eng.traineddata` next to the test assembly - same
/// requirement the CLI's own `--ocr yes` has (see README), fetched the same
/// way. QrLinkPdf.Tests.fsproj copies it in from the repo-root `tessdata/`
/// used for local dev if present; CI fetches it fresh before running tests
/// (see deploy.yml). Fails loudly rather than skipping quietly if it's
/// missing - a silently-skipped test here would defeat the point.
module QrLinkPdf.Tests.OcrTests

open System
open System.IO
open SkiaSharp
open Tesseract
open AnyUnit.Style.FSharp.Test
open AnyUnit.Style.Xunit
open QrLinkPdf

let private tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata")

/// Renders text onto a bitmap the same way the CLI/Wasm's own Ocr.fs feeds
/// Tesseract - no file, no codec, straight from SkiaSharp pixels via
/// SkiaPixConverter (see TesseractWords.ofBitmap) - matching what OCR
/// actually receives in this app (a rasterized PDF page), not a
/// Tesseract-friendly test fixture.
let private renderText (text: string) : SKBitmap =
    let bitmap = new SKBitmap(600, 150)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear(SKColors.White)
    use paint = new SKPaint(Color = SKColors.Black, IsAntialias = true)
    use font = new SKFont(SKTypeface.Default, 48f)
    canvas.DrawText(text, 20f, 90f, SKTextAlign.Left, font, paint)
    canvas.Flush()
    bitmap

let ``a real TesseractEngine recognizes real text via TesseractWords.ofBitmap`` () = test {
    let! Assert = assertion
    if not (File.Exists(Path.Combine(tessdataPath, "eng.traineddata"))) then
        failwith
            $"Missing {tessdataPath}/eng.traineddata - see README's OCR setup \
              (mkdir tessdata && curl ... eng.traineddata). These tests need \
              a real Tesseract engine, unlike the fake-engine tests in \
              Tests.fs."

    use engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default)
    use bitmap = renderText "HELLO TESSERACT"
    let words = TesseractWords.ofBitmap engine bitmap

    let recognized = words |> List.map (fun w -> w.Text) |> String.concat " "
    Assert.Contains("HELLO", recognized, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("TESSERACT", recognized, StringComparison.OrdinalIgnoreCase)

    // Every word should carry a real, non-degenerate bounding box - pins
    // that the SkiaSharp pixel interop is actually wiring up correctly, not
    // just that some text came back.
    for word in words do
        Assert.True(word.Box.Width > 0 && word.Box.Height > 0, $"Degenerate box for \"{word.Text}\"")
}

let ``a real TesseractEngine finds nothing worth reporting on a blank page`` () = test {
    let! Assert = assertion
    if not (File.Exists(Path.Combine(tessdataPath, "eng.traineddata"))) then
        failwith $"Missing {tessdataPath}/eng.traineddata - see README's OCR setup."

    use engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default)
    use bitmap = new SKBitmap(600, 150)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear(SKColors.White)
    canvas.Flush()

    let words = TesseractWords.ofBitmap engine bitmap
    Assert.Empty(words)
}
