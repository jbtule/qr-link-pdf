/// Desktop OCR engine, for pages whose text was flattened to vector
/// outlines by whatever produced the PDF - see QrLinkPdf.TextLinker for why
/// that needs OCR at all rather than smarter text extraction.
///
/// Uses the stock `Tesseract` NuGet package as-is, no fork. Its own native-
/// library loader only ever finds Windows binaries (its search is a custom
/// `InteropDotNet.LibraryLoader`, not .NET's real DllImport resolution, and
/// its platform detection is pointer-size-only - "x64" on every 64-bit OS,
/// Apple Silicon included). Rather than patch that, this points its public
/// `CustomSearchPath` extensibility hook at a directory of our own with the
/// real native libraries under the exact (hardcoded, version-specific)
/// filenames it expects - a real, supported mechanism, not a workaround.
module QrLinkPdf.Ocr

open System
open System.IO
open SkiaSharp
open Tesseract

/// What we hand the loader's `CustomSearchPath`. It always appends an "x64"
/// subfolder of its own accord - true on every 64-bit platform, Apple
/// Silicon included, since its platform detection is pointer-size-only (see
/// the module doc comment) - so the real files live one level deeper, at
/// `nativeLibDir/x64/...` (see `nativeLibFilesDir`), matching where
/// CopyTesseractNativeForAppleSilicon (the .fsproj) puts them.
let private nativeLibDir = Path.Combine(AppContext.BaseDirectory, "tesseract-native")

/// Where the actual files have to be, per the note on `nativeLibDir` above.
let private nativeLibFilesDir = Path.Combine(nativeLibDir, "x64")

/// Native library names this exact version of the `Tesseract` package's
/// (internal, so not directly referenceable) loader looks for, before its
/// own OS-specific prefix/extension get added ("lib" + name + ".dylib"/".so").
/// See the .fsproj's comment on the `Tesseract` PackageReference.
let private leptonicaName = "leptonica-1.82.0"
let private tesseractName = "tesseract50"

/// Well-known locations a real tesseract/leptonica install might already be
/// on macOS (Homebrew) or Linux (apt) - platforms with no bundled native
/// package. Best-effort: if none of these have both libraries, OCR just
/// stays unavailable, the same as if this whole function did nothing.
let private systemLibraryDirs =
    [ "/opt/homebrew/lib" // Homebrew, Apple Silicon
      "/usr/local/lib" // Homebrew, Intel Mac; also common on Linux
      "/usr/lib/x86_64-linux-gnu" // Debian/Ubuntu, apt
      "/usr/lib" ]

/// If Apple Silicon's bundled dylibs aren't there (every other platform),
/// look for a real system install and symlink it into `nativeLibDir` under
/// the names the loader expects. A no-op if `nativeLibDir` is already
/// populated, and harmless if nothing is found - `tryCreate`'s own probe is
/// what actually decides whether OCR ends up available.
let private linkSystemLibraries () =
    let extension = if OperatingSystem.IsMacOS() then ".dylib" else ".so"
    let leptonicaFile = Path.Combine(nativeLibFilesDir, "lib" + leptonicaName + extension)
    let tesseractFile = Path.Combine(nativeLibFilesDir, "lib" + tesseractName + extension)

    if not (File.Exists leptonicaFile) || not (File.Exists tesseractFile) then
        let findReal (soname: string) =
            systemLibraryDirs
            |> List.tryPick (fun dir ->
                // The real file is usually versioned (liblept.so.5,
                // libtesseract.5.dylib); an unversioned "lib<name><ext>"
                // symlink is what a -dev/header package normally provides,
                // so prefer that but fall back to a versioned match.
                let candidates =
                    if Directory.Exists dir then
                        Directory.GetFiles(dir, soname + "*" + extension) |> Array.sortBy String.length
                    else
                        [||]

                candidates |> Array.tryHead)

        Directory.CreateDirectory(nativeLibFilesDir) |> ignore

        // Leptonica's real library name is "lept", not "leptonica", on most
        // Linux distributions - try both.
        [ [ "leptonica"; "lept" ], leptonicaFile
          [ "tesseract" ], tesseractFile ]
        |> List.iter (fun (sonames, dest) ->
            if not (File.Exists dest) then
                sonames
                |> List.tryPick findReal
                |> Option.iter (fun source ->
                    try
                        File.CreateSymbolicLink(dest, source) |> ignore
                    with _ ->
                        ()))

/// One-time probe: is Tesseract actually usable? A missing native library
/// throws here, reliably and catchably.
let tryCreate (tessdataPath: string) : (SKBitmap -> OcrWord list) option =
    linkSystemLibraries ()
    InteropDotNet.LibraryLoader.Instance.CustomSearchPath <- nativeLibDir

    try
        let engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default)

        Some(fun (bitmap: SKBitmap) ->
            use image = SKImage.FromBitmap(bitmap)
            use data = image.Encode(SKEncodedImageFormat.Png, 100)
            use pix = Pix.LoadFromMemory(data.ToArray())
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

                  go <- iter.Next(PageIteratorLevel.Word) ])
    with ex ->
        // TesseractEngine's constructor throws through a layer of reflection
        // (InteropRuntimeImplementer builds its native bindings dynamically),
        // so the useful message - e.g. which library it couldn't find - is
        // on the inner exception, not this one.
        let reason = if isNull ex.InnerException then ex.Message else ex.InnerException.Message
        eprintfn "QRLINK_OCR requested but Tesseract isn't available (%s); continuing without OCR." reason
        None
