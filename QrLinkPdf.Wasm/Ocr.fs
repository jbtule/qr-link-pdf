/// Browser OCR engine, backed by tesseract-wasm's low-level, synchronous
/// OCREngine (wwwroot/ocr.js) rather than its Worker-based OCRClient - the
/// whole scan/link pipeline is synchronous (see QrLinkPdf.Core), and Blazor
/// WebAssembly runs .NET and JS on the same thread, so a real synchronous
/// JS call is available via IJSInProcessRuntime instead of needing async
/// interop everywhere OCR might run.
module QrLinkPdf.Wasm.Ocr

open System.Threading.Tasks
open Microsoft.JSInterop
open SkiaSharp
open QrLinkPdf

/// [<CLIMutable>] because System.Text.Json (which IJSInProcessRuntime.Invoke
/// uses under the hood) needs a parameterless constructor and public
/// settable properties to deserialize into - an ordinary F# record has
/// neither. Confirmed the hard way: without this, every call throws
/// "DeserializeNoConstructor" instead of returning a result. Not `private`:
/// a private type's CLIMutable-generated setters come out non-public too,
/// which System.Text.Json can't use either - confirmed that the hard way as
/// well.
[<CLIMutable>]
type OcrWordDto =
    { Left: int
      Top: int
      Right: int
      Bottom: int
      Text: string }

/// Loads the OCR engine and English model in the browser - fetches the WASM
/// module and ~4 MB of trained data, so this is the one async step. Safe to
/// call more than once; ocr.js only loads once.
let init (js: IJSRuntime) : Task<unit> =
    task { do! js.InvokeVoidAsync("qrLinkPdfOcr.init").AsTask() }

/// A ScanOptions.OcrEngine backed by the already-loaded engine from `init`.
let create (jsInProcess: IJSInProcessRuntime) : SKBitmap -> OcrWord list =
    fun bitmap ->
        // ocr.js wants a flat RGBA8888 buffer; converting here rather than
        // in JS keeps the colour-space handling on the .NET side, where
        // SkiaSharp already knows how to do it.
        use rgba = bitmap.Copy(SKColorType.Rgba8888)
        let words = jsInProcess.Invoke<OcrWordDto[]>("qrLinkPdfOcr.recognize", rgba.Bytes, rgba.Width, rgba.Height)

        [ for w in words -> { Text = w.Text; Box = SKRectI(w.Left, w.Top, w.Right, w.Bottom) } ]
