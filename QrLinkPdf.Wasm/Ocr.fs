/// Browser OCR engine, backed by a real `TesseractEngine` running in-process
/// under WebAssembly - the same `Tesseract.CrossPlatform` package the
/// desktop CLI uses (see ../Ocr.fs), via the new
/// `Tesseract.Native.browser-wasm` + `Tesseract.CrossPlatform.SkiaSharp`
/// preview packages (see QrLinkPdf.Wasm.fsproj's own comment - not yet
/// published to nuget.org). Replaces the previous JS-interop bridge to
/// tesseract-wasm (wwwroot/ocr.js, removed): PdfQrLinker's OcrEngine slot is
/// just `SKBitmap -> OcrWord list`, and TesseractEngine.Process is
/// synchronous, so there's no interop layer left to write here at all - the
/// whole scan/link pipeline stays synchronous the same way it already was.
module QrLinkPdf.Wasm.Ocr

open System
open System.IO
open System.Threading.Tasks
open Microsoft.JSInterop
open SkiaSharp
open Tesseract
open QrLinkPdf

let mutable private engine: TesseractEngine option = None

/// Loads the English trained-data model and constructs the engine - fetches
/// ~4 MB, so this is the one unavoidably async step; `Process` itself is
/// plain and synchronous once this is done. Safe to call more than once;
/// a no-op once `engine` is set.
let init (jsInProcess: IJSInProcessRuntime) : Task<unit> =
    task {
        if engine.IsNone then
            // Same asset-host convention index.html's loadBootResource hook
            // (and the old ocr.js) used - the page's own origin for local
            // dev, the Cloudflare asset host when deployed (see
            // qrLinkPdfGetAssetBase's own comment for the local-dev
            // fallback).
            let assetBase = jsInProcess.Invoke<string>("qrLinkPdfGetAssetBase")
            use http = new Net.Http.HttpClient(BaseAddress = Uri(assetBase))
            let! data = http.GetByteArrayAsync("tessdata/eng.traineddata")
            Directory.CreateDirectory("/tessdata") |> ignore
            do! File.WriteAllBytesAsync("/tessdata/eng.traineddata", data)
            engine <- Some(new TesseractEngine("/tessdata", "eng", EngineMode.Default))
    }

/// A ScanOptions.OcrEngine backed by the already-loaded engine from `init`.
/// Returns no words rather than throwing if `init` never succeeded (a
/// network hiccup, say) - same "fail this one scan's OCR quietly" contract
/// the old JS bridge had.
let create () : SKBitmap -> OcrWord list =
    fun bitmap ->
        match engine with
        | None -> []
        | Some engine ->
            // The browser-wasm native Tesseract build has no image codecs
            // linked in, so feed it already-decoded pixels directly via
            // SkiaSharp instead of Pix.LoadFromMemory (see
            // Tesseract.Native.browser-wasm's own README).
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
