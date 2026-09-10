/// Shared between the desktop CLI's Ocr.fs and QrLinkPdf.Wasm/Ocr.fs, which
/// both build on `Tesseract.CrossPlatform`/`Tesseract.CrossPlatform.SkiaSharp`
/// now (see each project's own PackageReference comment for why those moved
/// here rather than being declared separately in both - same reasoning as
/// itext7/PDFtoImage/ZXing already living here instead of in each host
/// project). What's genuinely platform-specific - constructing the engine
/// itself, sync on desktop vs. an async fetch-then-construct under wasm -
/// stays in each host's own Ocr.fs; this is just the part that's identical
/// once an engine already exists.
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
