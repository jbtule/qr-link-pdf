#!/bin/sh
# Publishes QrLinkPdf.Wasm and drives its real "Also try OCR" checkbox
# headlessly via Playwright (smoke-wasm-ocr/run-ocr-smoketest.mjs) - proves
# real Tesseract OCR actually works end to end under WebAssembly, including
# that Blazor's global error banner never shows up (see that script's own
# comment for why a build-only or console-text-only check already isn't
# enough here - this app has shipped a real, this-specific regression
# before). Exits non-zero if anything is wrong.
#
# Needs the wasm-tools workload, Node, and tessdata/eng.traineddata
# (README's OCR setup) - the last of these deliberately isn't fetched here:
# same "fail loudly, don't silently skip" reasoning as
# QrLinkPdf.Tests/OcrTests.fs.
set -e

configuration="${1:-Release}"
root=$(cd "$(dirname "$0")" && pwd)

if [ ! -f "$root/tessdata/eng.traineddata" ]; then
    echo "smoke-wasm-ocr.sh needs tessdata/eng.traineddata - see README's OCR setup (mkdir tessdata && curl ...)." >&2
    exit 1
fi

mkdir -p "$root/QrLinkPdf.Wasm/wwwroot/tessdata"
cp "$root/tessdata/eng.traineddata" "$root/QrLinkPdf.Wasm/wwwroot/tessdata/"

dotnet publish -c "$configuration" "$root/QrLinkPdf.Wasm/QrLinkPdf.Wasm.fsproj" -p:CompressionEnabled=false

cd "$root/smoke-wasm-ocr"
npm ci
npx playwright install --with-deps chromium
exec node run-ocr-smoketest.mjs "$root/QrLinkPdf.Wasm/bin/$configuration/net10.0-browser/publish/wwwroot"
