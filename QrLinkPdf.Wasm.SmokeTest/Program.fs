/// Runs QrLinkPdf.Core inside the WebAssembly runtime and reports whether each
/// native dependency actually worked. Exit code 0 means the browser build is
/// sound; anything else means it isn't.
module QrLinkPdf.Wasm.SmokeTest.Program

open System
open System.IO
open QrLinkPdf
open QrLinkPdf.Tests

let mutable private failures = 0

let private check name condition detail =
    if condition then
        printfn "  ok    %s" name
    else
        failures <- failures + 1
        printfn "  FAIL  %s - %s" name detail

/// Same reduced scan the browser app uses.
let private options =
    { ScanOptions.Default with
        Dpi = 200
        Scales = [ 1.0 ] }

[<EntryPoint>]
let main _ =
    printfn "QrLinkPdf WASM smoke test"
    printfn "  runtime: %s" (Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn "  os:      %s" (Runtime.InteropServices.RuntimeInformation.OSDescription)
    printfn ""

    try
        // Generating the fixture already exercises ZXing's encoder, SkiaSharp's
        // bitmap/PNG codecs and iText's writer.
        let expected = "https://example.com/wasm-smoke-test"
        let placement = TestPdfs.placement expected |> TestPdfs.at (140f, 560f) |> TestPdfs.sized 150f
        let pdf = TestPdfs.build [ placement ]
        check "fixture PDF generated" (pdf.Length > 0) "no bytes produced"

        use input = new MemoryStream(pdf)
        use output = new MemoryStream()
        let links = PdfQrLinker.link options input output
        let linked = output.ToArray()

        // PDFium rasterized the page and ZXing decoded what it found.
        check "one QR code found" (links.Length = 1) (sprintf "found %d" links.Length)

        match links with
        | [ link ] ->
            check "payload decoded correctly" (link.Uri = expected) (sprintf "got %s" link.Uri)

            // Coordinate mapping survived the trip through wasm.
            let centreX = link.Left + link.Width / 2.0
            let centreY = link.Bottom + link.Height / 2.0
            let wantX = float placement.Left + float placement.Size / 2.0
            let wantY = float placement.Bottom + float placement.Size / 2.0

            check
                "code located where it was drawn"
                (abs (centreX - wantX) < 12.0 && abs (centreY - wantY) < 12.0)
                (sprintf "centre (%.1f, %.1f), expected (%.1f, %.1f)" centreX centreY wantX wantY)
        | _ -> ()

        // iText wrote an annotated copy.
        check "linked PDF written" (linked.Length > pdf.Length) (sprintf "%d bytes in, %d out" pdf.Length linked.Length)

        // And the annotation is really in there.
        let hasUri =
            Text.Encoding.ASCII.GetString(linked).Contains("/URI")
            || Text.Encoding.ASCII.GetString(linked).Contains("URI")

        check "annotation present in output" hasUri "no /URI action found"

    with error ->
        failures <- failures + 1
        printfn "  FAIL  unhandled exception"
        printfn "%s" (string error)

    printfn ""

    if failures = 0 then
        printfn "PASS - QrLinkPdf.Core works under WebAssembly"
        0
    else
        printfn "FAIL - %d check(s) failed" failures
        1
