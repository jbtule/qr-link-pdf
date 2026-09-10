/// Desktop OCR engine, for pages whose text was flattened to vector
/// outlines by whatever produced the PDF - see QrLinkPdf.TextLinker for why
/// that needs OCR at all rather than smarter text extraction.
///
/// Uses `Tesseract.CrossPlatform` (github.com/jbtule/tesseract-nuget-platforms),
/// a patched fork of charlesw/tesseract packaged with real win-x64/win-arm64/
/// linux-x64/linux-arm64/osx-arm64 native binaries - same namespace/types as
/// the stock `Tesseract` package. Its loader finds those binaries on its
/// own (checks the flat output directory, then a runtimes/<rid>/native/
/// fallback, both with generic library names) - no search-path setup needed
/// here at all, unlike the stock package this replaces.
module QrLinkPdf.Ocr

open System
open SkiaSharp
open Tesseract

/// One-time probe: is Tesseract actually usable? A missing native library
/// throws here, reliably and catchably.
let tryCreate (tessdataPath: string) : (SKBitmap -> OcrWord list) option =
    try
        let engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default)

        Some(fun (bitmap: SKBitmap) ->
            // Tesseract.CrossPlatform.SkiaSharp's SkiaPixConverter feeds the
            // bitmap's pixels to Pix directly - no PNG-encode-then-decode
            // round trip through Pix.LoadFromMemory needed.
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

                  go <- iter.Next(PageIteratorLevel.Word) ])
    with ex ->
        // TesseractEngine's constructor throws through a layer of reflection
        // (InteropRuntimeImplementer builds its native bindings dynamically),
        // so the useful message - e.g. which library it couldn't find - is
        // on the inner exception, not this one.
        let reason = if isNull ex.InnerException then ex.Message else ex.InnerException.Message
        eprintfn "QRLINK_OCR requested but Tesseract isn't available (%s); continuing without OCR." reason
        None
