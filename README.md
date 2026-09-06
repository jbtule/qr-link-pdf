# qr-link-pdf

[![Deploy](https://github.com/jbtule/qr-link-pdf/actions/workflows/deploy.yml/badge.svg)](https://github.com/jbtule/qr-link-pdf/actions/workflows/deploy.yml)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-2f5d3f)](LICENSE)

A small F# library and command-line tool that finds QR codes and plain-text
URLs in a PDF and turns each one into a real, clickable hyperlink annotation
in the PDF — so a printed flyer that's been scanned back to PDF (or one where
the links were never live to begin with) becomes clickable on screen too.

## How it works

1. **[PDFtoImage](https://github.com/sungaila/PDFtoImage)** rasterizes each
   page of the PDF to a bitmap (via PDFium), with form-field rendering on —
   Acrobat's barcode fields draw the code in the field's appearance stream, so
   without it those pages rasterize with no QR code on them at all.
2. Each page is scanned for QR codes with **[ZXing.Net](https://github.com/micjahn/ZXing.Net)**.
   Rather than a single decode pass, the page is scanned at several
   downscaled resolutions (an image pyramid) and the results are merged —
   this is the same trick a phone camera scanner benefits from just by
   virtue of how the user frames the shot: ZXing's detector can miss a code
   that's too small a fraction of a huge page, and — less intuitively — can
   also miss one that's *too large* relative to the full-resolution canvas
   despite decoding fine once the image is shrunk down. Scanning multiple
   scales and merging catches both cases. Each level is also scanned with its
   colours inverted, so a code drawn as light modules on a dark background
   (dropped into a brand-coloured box, a common design choice) is found too.
3. Each decoded QR's pixel-space bounding box is converted into PDF
   point-space coordinates for the page it was found on.
4. Separately, **iText7** reads each page's actual text content and finds
   URL-shaped substrings sitting in ordinary prose (e.g. `Visit
   https://example.com today.`) — text that was never a live link. Any
   substring that's already covered by an existing link annotation is left
   alone, so re-running the tool never double-links the same text.
5. Optionally (`--ocr yes` on the CLI), **[Tesseract](https://github.com/tesseract-ocr/tesseract)**
   also runs OCR on the rasterized page and feeds what it finds through the
   same URL-matching logic — some PDF generators flatten body copy to vector
   outlines with no real text-showing operators at all, which step 4 can't
   see no matter how it's implemented. OCR results are merged with real-text
   results and deduplicated against each other.
6. **[iText7](https://github.com/itext/itext7-dotnet)** opens the original
   PDF and adds a borderless `Link` annotation over each QR code and each
   text-detected URL whose payload looks like a URL, pointing at that URL,
   then saves the result.

Only payloads that parse as an absolute URI (or start with `www.`) get
linked — this rule applies to text-detected URLs the same way it applies to
QR payloads; other QR content (vCards, Wi-Fi credentials, plain text, etc.)
is left alone.

## Usage

```sh
dotnet run -- <input.pdf> [output.pdf] [--debug yes|no] [--ocr yes|no] [--bare-domains yes|no]
```

`output.pdf` defaults to `<input>-linked.pdf` next to the input file if
omitted — the same name the browser app's download already uses. Every flag
defaults to `no` (the library's own default) if left out.

- `--debug yes` logs every QR code and text-detected URL candidate found on
  each page — including ones whose payload didn't pass the URL filter —
  along with its detected bounding box.
- `--ocr yes` also tries OCR on pages with no extractable text at all — some
  PDF generators flatten body copy to vector outlines instead of real text,
  which no amount of smarter text extraction can see. This needs:

  1. A real [Tesseract](https://github.com/tesseract-ocr/tesseract) install.
     On Apple Silicon this is bundled automatically at build time — nothing
     to do. Elsewhere, install it yourself: `brew install tesseract
     leptonica` on Intel Mac, `apt install tesseract-ocr libtesseract-dev`
     on Linux, or nothing on Windows (the .NET wrapper bundles its own DLLs
     there).
  2. English trained data next to the executable:
     ```sh
     mkdir -p tessdata
     curl -L -o tessdata/eng.traineddata \
       https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata
     ```

  If either is missing, `--ocr yes` prints one line explaining why and the
  run continues without OCR rather than failing.
- `--bare-domains yes` also links plain text shaped like a bare domain and
  path (`qrco.de/trails-end`), with no `https://` or `www.` to anchor on —
  see `MatchBareDomains` below for why this is off by default.

Example: `dotnet run -- flyer.pdf --ocr yes --bare-domains yes`.

## Library

All of the work lives in **QrLinkPdf.Core**, which exposes a stream-in /
stream-out API — nothing touches the filesystem unless you ask it to. The
command-line tool is a thin wrapper over it.

```fsharp
open System.IO
open QrLinkPdf

// Find the linkable QR codes and URL text without modifying anything.
use input = File.OpenRead "flyer.pdf"
let result = PdfQrLinker.scan ScanOptions.Default input
for link in result.Links do
    printfn "page %d: %s at (%f, %f)" link.PageNumber link.Uri link.Left link.Bottom

// Or write an annotated copy, and get back what was added.
use input = File.OpenRead "flyer.pdf"
use output = File.Create "flyer-linked.pdf"
let result = PdfQrLinker.link ScanOptions.Default input output
```

`scan` and `link` read `input` to the end and leave both streams open, so the
caller stays in charge of their lifetime. `PdfQrLinker.linkFile` is a
file-path convenience wrapper over `link`.

All three return a `ScanResult`: `Links` is what got (or would get) a new
link annotation, `AlreadyLinked` is everything found that already sits under
a live hyperlink and was left alone — a re-run over `link`'s own output
finds the same codes and text again, but reports them as `AlreadyLinked`
rather than annotating them a second time.

Behaviour is tuned through `ScanOptions`:

| Field | Default | |
| --- | --- | --- |
| `Dpi` | `400` | Resolution each page is rasterized at for scanning. |
| `Scales` | `[1.0; 0.6; 0.4; 0.25]` | The scan pyramid: each page is decoded at every level and the results merged. |
| `UriFilter` | absolute URIs, plus `www.` upgraded to https | Decides which payloads get linked, and to what — applied to both QR payloads and text-detected URL candidates. Return `None` to skip one. |
| `MatchBareDomains` | `false` | Also link plain text shaped like a bare domain and path (`qrco.de/trails-end`), with no `https://` or `www.` to anchor on. Off by default — unlike the scheme-anchored case, this is a shape heuristic (a known-TLD check plus a required `/path`) with a small false-positive risk, e.g. a decimal figure or version number followed by a slash. |
| `OcrEngine` | `None` | Recognizes text in a rasterized page bitmap, for pages with no real text-showing operators to extract. `None` disables OCR — every real implementation is platform-specific, so there's no built-in default; the CLI supplies one via `--ocr yes`. |
| `Trace` | `ignore` | Receives diagnostic lines (what the CLI's `--debug yes` hooks up to). |

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

### OCR in the browser

The "Also try OCR" checkbox is backed by
[tesseract-wasm](https://github.com/robertknight/tesseract-wasm)'s low-level,
synchronous engine, run entirely locally the same way everything else here
is — nothing is uploaded. For local dev, vendor its files and get English
trained data once:

```sh
npm pack tesseract-wasm@0.11.0
tar xzf tesseract-wasm-0.11.0.tgz
mkdir -p QrLinkPdf.Wasm/wwwroot/tesseract-wasm
cp package/dist/lib.js package/dist/tesseract-core*.wasm QrLinkPdf.Wasm/wwwroot/tesseract-wasm/
rm -rf package tesseract-wasm-0.11.0.tgz

mkdir -p QrLinkPdf.Wasm/wwwroot/tessdata
curl -L -o QrLinkPdf.Wasm/wwwroot/tessdata/eng.traineddata \
  https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata
```

Neither is checked in — same reasoning as the CLI's `tessdata/`. In the
deployed app these are hosted on the Cloudflare asset project alongside the
.NET runtime, not GitHub Pages.

### Icons

The favicon, `apple-touch-icon.png` (iOS "Add to Home Screen"), and the
`icon-192.png`/`icon-512.png` pair in `manifest.webmanifest` (Android/PWA
"Add to Home Screen") are all generated from `graphics/icon-orig-file.png`
by `graphics/make-icons.py`. Re-run it after changing the source image:

```sh
python3 graphics/make-icons.py
```

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/) (10.0+).

```sh
dotnet build
```

That builds the CLI and the library. The browser app is deliberately left out
of the default build so the `wasm-tools` workload isn't needed just to build
the command-line tool — build it explicitly with
`dotnet build QrLinkPdf.Wasm/QrLinkPdf.Wasm.fsproj`.

## Tests

```sh
dotnet test QrLinkPdf.Tests
```

The tests generate their own PDFs rather than checking in fixture files:
[TestPdfs.fs](QrLinkPdf.Tests/TestPdfs.fs) draws QR codes at chosen positions
on generated pages, so a test can state where it put a code and assert on where
the scanner says it found it. That covers the parts most likely to break
quietly — the image-to-PDF Y flip, page numbering, and the URL filter — and it
means no real-world document has to be published to have a regression suite.

[DegradedTests.fs](QrLinkPdf.Tests/DegradedTests.fs) roughs the generated codes
up first — rotating, downsampling, fading and JPEG-mangling them — because
crisp fixtures never exercise the multi-scale pyramid in
[Scanner.fs](QrLinkPdf.Core/Scanner.fs) that exists for exactly that kind of
input. One test pins the pyramid's value directly: a washed-out code that a
single full-resolution pass cannot see, and the pyramid can.

### Proving the browser build

```sh
./smoke-wasm.sh
```

Compiles the library to WebAssembly and runs it under `node` — no browser, no
web server, no test framework — checking that a code is found, decoded,
located and annotated. It exits non-zero if anything is wrong, so it works as
a CI gate. This is the only automated check that covers the Emscripten
static-archive linking of PDFium and Skia; a normal test run cannot, because
it uses the desktop native libraries instead. Needs the `wasm-tools` workload.

## Deploying

The browser app is published by [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml)
on every push to `main`, split across two hosts:

| | Serves | Size |
| --- | --- | --- |
| GitHub Pages | `index.html`, CSS, and the Blazor bootstrap script | ~65 KB |
| Cloudflare Pages | the .NET runtime, PDFium, Skia and every assembly | ~27 MB |

The page keeps its `tools.tuley.name/qr-link-pdf` address while the heavy boot
resources come from a host that serves brotli and doesn't meter bandwidth —
GitHub Pages only gzips and has a 100 GB/month soft limit. `index.html`
redirects them with Blazor's [`loadBootResource`](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/startup#load-client-side-boot-resources)
hook, and the asset host sends `Access-Control-Allow-Origin` so the
cross-origin fetches and Blazor's integrity checks both succeed.

Locally nothing is split: `window.qrLinkPdfAssetBase` is empty, so
`dotnet run --project QrLinkPdf.Wasm` loads everything from one origin.

One-time setup:

1. Repository **Settings → Pages → Source: GitHub Actions**.
2. A Cloudflare Pages project named `qr-link-pdf-assets` (direct upload, no
   build step).
3. Repository secrets `CLOUDFLARE_API_TOKEN` (with the *Cloudflare Pages: Edit*
   permission) and `CLOUDFLARE_ACCOUNT_ID`.

The asset host and base path are the `ASSET_BASE` and `BASE_HREF` variables at
the top of the workflow. Assets deploy before the page, because the page
references fingerprinted filenames that must already exist.

### Running CI locally

```sh
./ci-local.sh
```

Replays the workflow's build job on Linux in a container, against your working
tree rather than `HEAD`, so you can answer "would CI pass?" before pushing. It
extracts the job's own `run:` steps out of the workflow instead of
reimplementing them, so the two can't drift.

Needs [Apple's `container`](https://github.com/apple/container) (macOS 26+) or
Docker. The first run builds an image with the `wasm-tools` workload baked in
and takes a few minutes; later runs reuse it.

This is worth having because the interesting failures are all
platform-specific and invisible on macOS — a SkiaSharp native asset that only
resolves wrong on Linux, an Emscripten link that needs `python` on `PATH`, a
`node` too old to treat `dotnet.js` as an ES module.

## License

[GNU AGPL v3](LICENSE)

---

🤖 Written with [Claude Code](https://claude.com/claude-code).
