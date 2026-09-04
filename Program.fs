module QrLinkPdf.Program

open System
open QrLinkPdf

[<EntryPoint>]
let main argv =
    match argv with
    | [| inputPath; outputPath |] ->
        // Set QRLINK_DEBUG=1 to log every QR code found per page (box +
        // payload) before the URL filter is applied.
        let debug = Environment.GetEnvironmentVariable("QRLINK_DEBUG") = "1"

        let options =
            { ScanOptions.Default with
                Trace = if debug then eprintfn "[debug] %s" else ignore }

        let links = PdfQrLinker.linkFile options inputPath outputPath

        if links.IsEmpty then
            printfn "No QR codes with URL/URI payloads were found."
        else
            for link in links do
                printfn "Page %d: linking QR code to %s" link.PageNumber link.Uri

        printfn "Wrote %s (%d link%s added)" outputPath links.Length (if links.Length = 1 then "" else "s")
        0
    | _ ->
        eprintfn "Usage: QrLinkPdf <input.pdf> <output.pdf>"
        1
