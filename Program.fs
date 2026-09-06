module QrLinkPdf.Program

open System
open QrLinkPdf

[<EntryPoint>]
let main argv =
    match argv with
    | [| inputPath; outputPath |] ->
        // Set QRLINK_DEBUG=1 to log every QR code and text-detected URL
        // candidate found per page before the URL filter is applied.
        let debug = Environment.GetEnvironmentVariable("QRLINK_DEBUG") = "1"

        // Set QRLINK_OCR=1 to also try OCR on pages with no extractable text
        // at all - some PDF generators flatten body copy to vector outlines,
        // which no text-extraction approach can see. Needs a real Tesseract
        // install; see README for setup. Degrades to "off" if unavailable
        // rather than failing the run.
        let ocrRequested = Environment.GetEnvironmentVariable("QRLINK_OCR") = "1"

        let ocrEngine =
            if ocrRequested then
                let tessdataPath = IO.Path.Combine(AppContext.BaseDirectory, "tessdata")
                Ocr.tryCreate tessdataPath
            else
                None

        // Set QRLINK_BARE_DOMAINS=1 to also link plain text shaped like a
        // bare domain and path (qrco.de/trails-end), with no https:// or
        // www. to anchor on. Off by default: unlike the scheme-anchored
        // case, this is a shape heuristic with a small false-positive risk.
        let bareDomains = Environment.GetEnvironmentVariable("QRLINK_BARE_DOMAINS") = "1"

        let options =
            { ScanOptions.Default with
                OcrEngine = ocrEngine
                MatchBareDomains = bareDomains
                Trace = if debug then eprintfn "[debug] %s" else ignore }

        let links = PdfQrLinker.linkFile options inputPath outputPath

        if links.IsEmpty then
            printfn "No QR codes or plain-text URLs worth linking were found."
        else
            for link in links do
                printfn "Page %d: linking to %s" link.PageNumber link.Uri

        printfn "Wrote %s (%d link%s added)" outputPath links.Length (if links.Length = 1 then "" else "s")
        0
    | _ ->
        eprintfn "Usage: QrLinkPdf <input.pdf> <output.pdf>"
        1
