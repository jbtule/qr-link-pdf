module QrLinkPdf.Program

open System
open QrLinkPdf

/// Every optional behavior as one yes/no flag, defaulting to the library's
/// own default when omitted.
type private Flags =
    { Debug: bool
      Ocr: bool
      BareDomains: bool }

let private defaultFlags = { Debug = false; Ocr = false; BareDomains = false }

let private usage =
    "Usage: qr-link-pdf <input.pdf|.png|.jpg> [output.pdf] [--debug yes|no] [--ocr yes|no] [--bare-domains yes|no]"

/// MinVer stamps the git-derived version (see QrLinkPdf.fsproj) into the
/// informational version attribute; the plain assembly version is only
/// ever the major.minor.patch part.
let private version =
    let attr =
        Reflection.Assembly.GetExecutingAssembly()
        |> System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Reflection.AssemblyInformationalVersionAttribute>

    if isNull attr then "unknown" else attr.InformationalVersion

/// Where to look for `eng.traineddata`, first hit wins: TESSDATA_PREFIX
/// (Tesseract's own convention), a `tessdata/` in the current directory,
/// then one next to the executable. The last is the only one that used to
/// be checked, and it's the wrong answer for a `dotnet tool install` - the
/// executable then lives under ~/.dotnet/tools/.store/<pkg>/<version>/...,
/// which nobody should have to find, and which moves on every upgrade.
let private tessdataCandidates () =
    [ match Environment.GetEnvironmentVariable "TESSDATA_PREFIX" with
      | null
      | "" -> ()
      | prefix -> prefix
      IO.Path.Combine(Environment.CurrentDirectory, "tessdata")
      IO.Path.Combine(AppContext.BaseDirectory, "tessdata") ]

let private findTessdata () =
    let candidates = tessdataCandidates ()

    match candidates |> List.tryFind (fun dir -> IO.File.Exists(IO.Path.Combine(dir, "eng.traineddata"))) with
    | Some dir -> dir
    | None ->
        // Fall through to the last candidate so Ocr.tryCreate's own
        // "continuing without OCR" message names a concrete path.
        List.last candidates

/// Recognized image extensions, wrapped in a one-page PDF via ImageToPdf
/// before anything else sees them - everything past this point (including
/// error messages below) only ever deals in PDF bytes. Anything else is
/// assumed to already be a PDF and passed through unchanged; iText's own
/// error if that assumption is wrong is clear enough on its own.
let private imageExtensions = set [ ".png"; ".jpg"; ".jpeg"; ".webp"; ".gif"; ".bmp" ]

let private readInputBytes (inputPath: string) : byte[] =
    let bytes = IO.File.ReadAllBytes inputPath

    if imageExtensions.Contains(IO.Path.GetExtension(inputPath).ToLowerInvariant()) then
        ImageToPdf.convert bytes
    else
        bytes

let private parseYesNo (name: string) (value: string) =
    match value.ToLowerInvariant() with
    | "yes" -> Ok true
    | "no" -> Ok false
    | _ -> Error(sprintf "--%s must be 'yes' or 'no', got '%s'" name value)

let rec private parseFlags (flags: Flags) args =
    match args with
    | [] -> Ok flags
    | "--debug" :: v :: rest -> parseYesNo "debug" v |> Result.bind (fun b -> parseFlags { flags with Debug = b } rest)
    | "--ocr" :: v :: rest -> parseYesNo "ocr" v |> Result.bind (fun b -> parseFlags { flags with Ocr = b } rest)
    | "--bare-domains" :: v :: rest ->
        parseYesNo "bare-domains" v
        |> Result.bind (fun b -> parseFlags { flags with BareDomains = b } rest)
    | unknown :: _ -> Error(sprintf "Unknown argument: %s" unknown)

/// Positional paths (1 or 2) come before any flags (which all start with
/// "--"), so split on that rather than a fixed-arity pattern match.
let rec private splitPaths args =
    match args with
    | (a: string) :: rest when not (a.StartsWith "--") ->
        let more, flags = splitPaths rest
        a :: more, flags
    | _ -> [], args

/// Same "-linked.pdf" naming the browser app already downloads its result
/// as, so a caller who doesn't care what it's called doesn't have to spell
/// one out - placed next to the input rather than in the current directory,
/// since a CLI run isn't scoped to "the download folder" the way a browser is.
let private defaultOutputPath (inputPath: string) =
    let dir = IO.Path.GetDirectoryName(inputPath)
    let name = IO.Path.GetFileNameWithoutExtension(inputPath) + "-linked.pdf"
    if String.IsNullOrEmpty dir then name else IO.Path.Combine(dir, name)

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [ "--version" ] ->
        printfn "%s" version
        exit 0
    | [ "--help" ]
    | [ "-h" ]
    | [] ->
        printfn "%s" usage
        exit 0
    | _ -> ()

    let paths, flagArgs = splitPaths (List.ofArray argv)

    let resolvedPaths =
        match paths with
        | [ inputPath ] -> Ok(inputPath, defaultOutputPath inputPath)
        | [ inputPath; outputPath ] -> Ok(inputPath, outputPath)
        | _ -> Error "Expected 1 or 2 file paths."

    let result =
        resolvedPaths
        |> Result.bind (fun paths -> parseFlags defaultFlags flagArgs |> Result.map (fun flags -> paths, flags))

    match result with
    | Error message ->
        eprintfn "%s" message
        eprintfn "%s" usage
        1
    | Ok((inputPath, outputPath), flags) ->
        let ocrEngine =
            if flags.Ocr then
                Ocr.tryCreate (findTessdata ())
            else
                None

        let options =
            { ScanOptions.Default with
                OcrEngine = ocrEngine
                MatchBareDomains = flags.BareDomains
                Trace = if flags.Debug then eprintfn "[debug] %s" else ignore }

        use input = new IO.MemoryStream(readInputBytes inputPath)
        use output = IO.File.Create(outputPath)
        let result = PdfQrLinker.link options input output

        if result.Links.IsEmpty && result.AlreadyLinked.IsEmpty then
            printfn "No QR codes or plain-text URLs worth linking were found."
        else
            for link in result.Links do
                printfn "Page %d: linking to %s" link.PageNumber link.Uri

            for already in result.AlreadyLinked do
                printfn "Page %d: %s is already linked, left alone" already.PageNumber already.Uri

        printfn
            "Wrote %s (%d link%s added)"
            outputPath
            result.Links.Length
            (if result.Links.Length = 1 then "" else "s")

        0
