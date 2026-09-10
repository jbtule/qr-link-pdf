/// Shared between the desktop CLI's Ocr.fs and QrLinkPdf.Wasm/Ocr.fs -
/// source-included into both projects (see their own .fsproj, same pattern
/// QrLinkPdf.Wasm.SmokeTest.fsproj already uses for TestPdfs.fs) rather than
/// a separate library project, since it's one function. Not part of
/// QrLinkPdf.Core: Core deliberately has no Tesseract dependency, keeping
/// ScanOptions.OcrEngine as a plain `SKBitmap -> OcrWord list` so it stays
/// engine-agnostic.
module QrLinkPdf.TesseractWords

open System
open SkiaSharp
open Tesseract

/// Runs a decoded bitmap through an already-constructed TesseractEngine and
/// collects its recognized words - the part that's identical on desktop and
/// under browser-wasm now that both go through the same Tesseract.CrossPlatform
/// engine and Tesseract.CrossPlatform.SkiaSharp's direct SKBitmap/Pix interop
/// (no PNG round trip, no codec dependency - the wasm native build has none).
let ofBitmap (engine: TesseractEngine) (bitmap: SKBitmap) : OcrWord list =
    use pix = SkiaPixConverter.ToPix(bitmap)
    use page = engine.Process(pix)
    use iter = page.GetIterator()
    iter.Begin()

    [ let mutable go = true

      while go do
          match iter.TryGetBoundingBox(PageIteratorLevel.Word) with
          | true, rect ->
              let text = iter.GetText(PageIteratorLevel.Word)

              if not (String.IsNullOrWhiteSpace text) then
                  yield
                      { Text = text.Trim()
                        Box = SKRectI(rect.X1, rect.Y1, rect.X2, rect.Y2) }
          | false, _ -> ()

          go <- iter.Next(PageIteratorLevel.Word) ]
