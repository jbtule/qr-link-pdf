# qr-link-pdf

A small F# command-line tool that finds QR codes in a PDF and turns each one
into a real, clickable hyperlink annotation in the PDF — so a printed flyer
that's been scanned back to PDF (or one where the links were never live to
begin with) becomes clickable on screen too.

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

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/) (9.0+).

```sh
dotnet build
```

## License

[GNU AGPL v3](LICENSE), to comply with iText7's AGPL licensing.
