# qr-link-pdf

A small F# library and command-line tool that finds QR codes in a PDF and
turns each one into a real, clickable hyperlink annotation in the PDF — so a
printed flyer that's been scanned back to PDF (or one where the links were
never live to begin with) becomes clickable on screen too.

## How it works

1. **[PDFtoImage](https://github.com/sungaila/PDFtoImage)** rasterizes each
   page of the PDF to a bitmap (via PDFium).
2. Each page is scanned for QR codes with **[ZXing.Net](https://github.com/micjahn/ZXing.Net)**.
   Rather than a single decode pass, the page is scanned at several
   downscaled resolutions (an image pyramid) and the results are merged —
   this is the same trick a phone camera scanner benefits from just by
   virtue of how the user frames the shot: ZXing's detector can miss a code
   that's too small a fraction of a huge page, and — less intuitively — can
   also miss one that's *too large* relative to the full-resolution canvas
   despite decoding fine once the image is shrunk down. Scanning multiple
   scales and merging catches both cases.
3. Each decoded QR's pixel-space bounding box is converted into PDF
   point-space coordinates for the page it was found on.
4. **[iText7](https://github.com/itext/itext7-dotnet)** opens the original
   PDF and adds a borderless `Link` annotation over each QR code whose
   payload looks like a URL, pointing at that URL, then saves the result.

Only QR payloads that parse as an absolute URI (or start with `www.`) get
linked; other QR content (vCards, Wi-Fi credentials, plain text, etc.) is
left alone.

## Usage

```sh
dotnet run -- <input.pdf> <output.pdf>
```

Set `QRLINK_DEBUG=1` to log every QR code found on each page — including
ones whose payload didn't pass the URL filter — along with its detected
bounding box.

## Library

All of the work lives in **QrLinkPdf.Core**, which exposes a stream-in /
stream-out API — nothing touches the filesystem unless you ask it to. The
command-line tool is a thin wrapper over it.

```fsharp
open System.IO
open QrLinkPdf

// Find the linkable QR codes without modifying anything.
use input = File.OpenRead "flyer.pdf"
let found = PdfQrLinker.scan ScanOptions.Default input
for link in found do
    printfn "page %d: %s at (%f, %f)" link.PageNumber link.Uri link.Left link.Bottom

// Or write an annotated copy, and get back the links that were added.
use input = File.OpenRead "flyer.pdf"
use output = File.Create "flyer-linked.pdf"
let added = PdfQrLinker.link ScanOptions.Default input output
```

`scan` and `link` read `input` to the end and leave both streams open, so the
caller stays in charge of their lifetime. `PdfQrLinker.linkFile` is a
file-path convenience wrapper over `link`.

Behaviour is tuned through `ScanOptions`:

| Field | Default | |
| --- | --- | --- |
| `Dpi` | `400` | Resolution each page is rasterized at for scanning. |
| `Scales` | `[1.0; 0.6; 0.4; 0.25]` | The scan pyramid: each page is decoded at every level and the results merged. |
| `UriFilter` | absolute URIs, plus `www.` upgraded to https | Decides which payloads get linked, and to what. Return `None` to skip a code. |
| `Trace` | `ignore` | Receives diagnostic lines (what the CLI's `QRLINK_DEBUG` hooks up to). |

```fsharp
// e.g. faster scanning, and only link your own domain
{ ScanOptions.Default with
    Dpi = 200
    Scales = [ 1.0; 0.5 ]
    UriFilter = fun text -> if text.StartsWith "https://example.com/" then Some text else None }
```

`Scanner.findOnBitmap` is public too, if you want the QR detection against an
`SKBitmap` without any PDF involved.

## Web app

`QrLinkPdf.Wasm` is the same library running entirely in the browser — a
[Bolero](https://fsbolero.io/) (F# Blazor WebAssembly) page where you pick a
PDF and get the linked copy back. Nothing is uploaded anywhere; PDFium, Skia
and the whole .NET runtime are compiled to WebAssembly and run locally.

It needs the `wasm-tools` workload, because PDFium and Skia ship as Emscripten
static archives that get linked into `dotnet.wasm` at build time:

```sh
sudo dotnet workload install wasm-tools
dotnet run --project QrLinkPdf.Wasm
```

The first build relinks the runtime with Emscripten, which takes a while;
after that it's incremental.

The browser scans at 300 DPI over two pyramid levels rather than the CLI's 400
DPI over four, which finds the same codes several times faster. Don't lower it
further — at 200 DPI the test handout drops from 7 codes found to 5.

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/) (10.0+).

```sh
dotnet build
```

That builds the CLI and the library. The browser app is deliberately left out
of the default build so the `wasm-tools` workload isn't needed just to build
the command-line tool — build it explicitly with
`dotnet build QrLinkPdf.Wasm/QrLinkPdf.Wasm.fsproj`.

## License

[GNU AGPL v3](LICENSE)

---

🤖 Written with [Claude Code](https://claude.com/claude-code).
