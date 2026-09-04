#!/bin/sh
# Builds QrLinkPdf.Core for WebAssembly and runs it under node, proving PDFium,
# Skia, ZXing and iText all link and execute in the browser runtime.
# Exits non-zero if anything is wrong. Needs the wasm-tools workload.
set -e

configuration="${1:-Release}"
root=$(cd "$(dirname "$0")" && pwd)

dotnet build -c "$configuration" "$root/QrLinkPdf.Wasm.SmokeTest/QrLinkPdf.Wasm.SmokeTest.fsproj"

cd "$root/QrLinkPdf.Wasm.SmokeTest/bin/$configuration/net10.0-browser/wwwroot"
exec node runtests.mjs
