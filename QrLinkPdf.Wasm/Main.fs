module QrLinkPdf.Wasm.Main

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Forms
open Microsoft.JSInterop
open Elmish
open Bolero
open Bolero.Html
open PDFtoImage
open SkiaSharp
open iText.Kernel.Pdf
open QrLinkPdf

/// IBrowserFile.OpenReadStream defaults to a 512 KB cap, which any real PDF
/// blows straight through.
let private maxUploadBytes = 64L * 1024L * 1024L

/// ZXing decodes interpreted in the browser, so use the lighter scan.
let private browserOptions trace ocrEngine bareDomains =
    { ScanOptions.Interactive with
        Trace = trace
        OcrEngine = ocrEngine
        MatchBareDomains = bareDomains }

type State =
    | Idle
    | ReadingFile
    /// File buffered (and a thumbnail attempted), waiting for Process.
    | Ready
    | Working of string
    | Done of ScanResult
    | Failed of string

/// Whether a finding got a new link annotation, or was already linked and
/// left alone.
type FindingStatus =
    | Linked
    | AlreadyLinked

/// A lettered, positioned finding - a QR code or plain-text URL run - ready
/// to be shown in the results list and drawn as a box over its page's
/// thumbnail once results are in. The same letter identifies the same
/// finding in both places.
type Finding =
    { Letter: char
      PageNumber: int
      Uri: string
      Left: float
      Bottom: float
      Width: float
      Height: float
      Status: FindingStatus }

/// One page's thumbnail, rendered after a scan for every page that has at
/// least one finding - not the whole document, since a long PDF with a
/// couple of QR codes on it shouldn't force paging through pages with
/// nothing to see.
type PageThumbnail =
    { PageNumber: int
      Src: string
      PageSize: float * float }

type Model =
    { State: State
      FileName: string
      FileBytes: byte[] option
      /// data:image/png;base64,... of the PDF's first page and that page's
      /// point-size (for positioning finding overlays), or None if rendering
      /// it failed - never blocks choosing/processing the file. Shown before
      /// Process is clicked, when there are no findings yet to page through.
      Thumbnail: (string * (float * float)) option
      /// One thumbnail per page with a finding on it, populated once a scan
      /// finishes. Paged through with CurrentThumbnail once there's more
      /// than one.
      ResultThumbnails: PageThumbnail list
      CurrentThumbnail: int
      Findings: Finding list
      Output: byte[] option
      Log: string list
      OcrEnabled: bool
      BareDomainsEnabled: bool }

let initModel =
    { State = Idle
      FileName = ""
      FileBytes = None
      Thumbnail = None
      ResultThumbnails = []
      CurrentThumbnail = 0
      Findings = []
      Output = None
      Log = []
      OcrEnabled = false
      BareDomainsEnabled = false }

type Message =
    | FileChosen of IBrowserFile
    | Loaded of name: string * bytes: byte[] * thumbnail: (string * (float * float)) option
    | ProcessClicked
    | Finished of output: byte[] * result: ScanResult * thumbnails: PageThumbnail list * log: string list
    | Errored of exn
    | Download
    | Reset
    | OcrToggled of bool
    | BareDomainsToggled of bool
    | SettingsLoaded of ocr: bool * bareDomains: bool
    | NextResultPage
    | PrevResultPage

/// Renders one page (0-based index), small enough to be cheap but large
/// enough to actually read once the CSS caps its display width, plus that
/// page's point-size (needed to position finding overlays over it). Failure
/// (a corrupt PDF, an out-of-range page) degrades to no thumbnail rather
/// than blocking anything.
let private tryRenderThumbnailForPage (bytes: byte[]) (pageIndex: int) : (string * (float * float)) option =
    try
        use doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))
        let size = doc.GetPage(pageIndex + 1).GetPageSize()

        use bitmap =
            Conversion.ToImage(bytes, Index(pageIndex), options = RenderOptions(Dpi = 100, WithAnnotations = false, WithFormFill = true))

        use image = SKImage.FromBitmap(bitmap)
        use data = image.Encode(SKEncodedImageFormat.Png, 100)

        Some(
            "data:image/png;base64," + Convert.ToBase64String(data.ToArray()),
            (float (size.GetWidth()), float (size.GetHeight()))
        )
    with _ ->
        None

/// Thumbnails for exactly the pages that ended up with a finding on them -
/// re-rasterizing here rather than threading bitmaps out of
/// PdfQrLinker.scan/link keeps Core's API free of a browser-only display
/// concern. Cheap enough to redo: the same low DPI as the pre-Process
/// preview, and only for pages that actually have something to show.
let private renderResultThumbnails (bytes: byte[]) (pageNumbers: int list) : PageThumbnail list =
    pageNumbers
    |> List.choose (fun pageNumber ->
        tryRenderThumbnailForPage bytes (pageNumber - 1)
        |> Option.map (fun (src, size) ->
            { PageNumber = pageNumber
              Src = src
              PageSize = size }))

/// Letter every finding - newly linked and already-linked alike - across the
/// whole document (page, then top-to-bottom on that page), so the same
/// letter identifies the same finding in the results list and in the
/// thumbnail overlay.
let private letterFindings (result: ScanResult) : Finding list =
    (result.Links |> List.map (fun l -> l, Linked))
    @ (result.AlreadyLinked |> List.map (fun l -> l, AlreadyLinked))
    |> List.sortBy (fun (l, _) -> l.PageNumber, -l.Bottom) // page, then top-to-bottom
    |> List.mapi (fun i (l, status) ->
        { Letter = char (int 'A' + i)
          PageNumber = l.PageNumber
          Uri = l.Uri
          Left = l.Left
          Bottom = l.Bottom
          Width = l.Width
          Height = l.Height
          Status = status })

/// Recognized image extensions - wrapped in a one-page PDF via
/// ImageToPdf.convert right after buffering below, so everything past that
/// point (thumbnail rendering, scanning, download) only ever deals in PDF
/// bytes, same as the CLI's own readInputBytes.
let private imageExtensions = set [ ".png"; ".jpg"; ".jpeg"; ".webp"; ".gif"; ".bmp" ]

/// The browser's file stream only supports async reads, and QrLinkPdf.Core
/// reads synchronously - so buffer here first. It costs nothing: Core's
/// readAll short-circuits on a MemoryStream.
let private readFileAndThumbnail (file: IBrowserFile) =
    task {
        use source = file.OpenReadStream(maxAllowedSize = maxUploadBytes)
        let buffer = new MemoryStream()
        do! source.CopyToAsync(buffer)
        let rawBytes = buffer.ToArray()
        // Let the "Reading..." render land before the image-to-PDF
        // conversion (or the thumbnail render below) blocks the only thread.
        do! Task.Yield()

        let bytes =
            if imageExtensions.Contains(Path.GetExtension(file.Name).ToLowerInvariant()) then
                ImageToPdf.convert rawBytes
            else
                rawBytes

        return file.Name, bytes, tryRenderThumbnailForPage bytes 0
    }

let private processFile (jsInProcess: IJSInProcessRuntime) (ocrEnabled: bool) (bareDomains: bool) (bytes: byte[]) =
    task {
        do! Task.Yield()

        if ocrEnabled then
            // A no-op if the startup load (see QrApp.Program) already
            // succeeded - this only does real work in the rare case a scan
            // starts before that finishes, or if it never does. Either way
            // a failure here just means this scan runs without OCR;
            // Ocr.create itself returns no words rather than throwing on a
            // still-unloaded engine.
            try
                do! Ocr.init jsInProcess
            with _ ->
                ()

        let log = ResizeArray<string>()
        let ocrEngine = if ocrEnabled then Some(Ocr.create ()) else None
        use input = new MemoryStream(bytes)
        use output = new MemoryStream()
        let result = PdfQrLinker.link (browserOptions log.Add ocrEngine bareDomains) input output
        // link's PdfDocument is disposed by the time it returns, so the bytes
        // are complete here.
        let output = output.ToArray()

        let pagesWithFindings =
            (result.Links @ result.AlreadyLinked) |> List.map (fun l -> l.PageNumber) |> List.distinct |> List.sort

        let thumbnails = renderResultThumbnails bytes pagesWithFindings
        return output, result, thumbnails, List.ofSeq log
    }

let private download (js: IJSRuntime) (fileName: string) (bytes: byte[]) =
    task {
        use stream = new MemoryStream(bytes)
        use reference = new DotNetStreamReference(stream, true)
        do! js.InvokeVoidAsync("qrLinkPdf.download", fileName, reference).AsTask()
    }

let private linkedName (name: string) =
    Path.GetFileNameWithoutExtension(name) + "-linked.pdf"

let private ocrEnabledKey = "qrLinkPdf.ocrEnabled"
let private bareDomainsEnabledKey = "qrLinkPdf.bareDomainsEnabled"

let private loadBoolSetting (js: IJSRuntime) (key: string) =
    task {
        let! value = js.InvokeAsync<string>("localStorage.getItem", key).AsTask()
        return value = "true"
    }

let private saveBoolSetting (js: IJSRuntime) (key: string) (value: bool) =
    // Not `string value`: F#'s bool.ToString() is "True"/"False", which
    // loadBoolSetting's lowercase comparison would never match - confirmed
    // the hard way (toggling a checkbox saved a value that could never read
    // back as true).
    task { do! js.InvokeVoidAsync("localStorage.setItem", key, (if value then "true" else "false")).AsTask() }

let update (js: IJSRuntime) (jsInProcess: IJSInProcessRuntime) message model =
    match message with
    | FileChosen file ->
        { model with
            State = ReadingFile
            FileName = file.Name
            FileBytes = None
            Thumbnail = None
            ResultThumbnails = []
            CurrentThumbnail = 0
            Findings = []
            Output = None
            Log = [] },
        Cmd.OfTask.either readFileAndThumbnail file Loaded Errored

    | Loaded(name, bytes, thumbnail) ->
        { model with
            State = Ready
            FileName = name
            FileBytes = Some bytes
            Thumbnail = thumbnail },
        Cmd.none

    | ProcessClicked ->
        match model.FileBytes with
        | None -> model, Cmd.none
        | Some bytes ->
            { model with State = Working "Scanning..." },
            Cmd.OfTask.either (processFile jsInProcess model.OcrEnabled model.BareDomainsEnabled) bytes Finished Errored

    | Finished(output, result, thumbnails, log) ->
        { model with
            State = Done result
            Findings = letterFindings result
            ResultThumbnails = thumbnails
            CurrentThumbnail = 0
            Output = Some output
            Log = log },
        Cmd.none

    | Errored error -> { model with State = Failed error.Message }, Cmd.none

    | Download ->
        match model.Output with
        | Some bytes -> model, Cmd.OfTask.attempt (download js (linkedName model.FileName)) bytes Errored
        | None -> model, Cmd.none

    | Reset ->
        { initModel with
            OcrEnabled = model.OcrEnabled
            BareDomainsEnabled = model.BareDomainsEnabled },
        Cmd.none

    | OcrToggled enabled ->
        { model with OcrEnabled = enabled }, Cmd.OfTask.attempt (saveBoolSetting js ocrEnabledKey) enabled (fun _ -> Reset)

    | BareDomainsToggled enabled ->
        { model with BareDomainsEnabled = enabled },
        Cmd.OfTask.attempt (saveBoolSetting js bareDomainsEnabledKey) enabled (fun _ -> Reset)

    | SettingsLoaded(ocr, bareDomains) ->
        { model with
            OcrEnabled = ocr
            BareDomainsEnabled = bareDomains },
        Cmd.none

    | NextResultPage ->
        { model with CurrentThumbnail = min (model.CurrentThumbnail + 1) (List.length model.ResultThumbnails - 1) },
        Cmd.none

    | PrevResultPage -> { model with CurrentThumbnail = max (model.CurrentThumbnail - 1) 0 }, Cmd.none

let private findingsList (extraClass: string) (findings: Finding list) =
    ul {
        attr.``class`` (sprintf "links %s" extraClass)

        forEach findings (fun f ->
            li {
                span {
                    attr.``class`` "letter"
                    string f.Letter
                }

                span {
                    attr.``class`` "page"
                    sprintf "Page %d" f.PageNumber
                }

                a {
                    attr.href f.Uri
                    attr.target "_blank"
                    f.Uri
                }
            })
    }

let private resultView (result: ScanResult) (findings: Finding list) dispatch =
    let linked = findings |> List.filter (fun f -> f.Status = Linked)
    let alreadyLinked = findings |> List.filter (fun f -> f.Status = AlreadyLinked)

    div {
        attr.``class`` "result"

        p {
            attr.``class`` "count"
            sprintf "Found %d clickable link%s to add." (List.length linked) (if List.length linked = 1 then "" else "s")
        }

        findingsList "linked" linked

        cond (List.isEmpty alreadyLinked)
        <| function
            | true -> empty ()
            | false ->
                div {
                    attr.``class`` "already-linked-section"

                    p {
                        attr.``class`` "count"

                        sprintf
                            "%d already linked, left alone."
                            (List.length alreadyLinked)
                    }

                    findingsList "already-linked" alreadyLinked
                }

        div {
            attr.``class`` "actions"

            button {
                attr.``class`` "primary"
                on.click (fun _ -> dispatch Download)
                "Download linked PDF"
            }

            button {
                on.click (fun _ -> dispatch Reset)
                "Start over"
            }
        }
    }

let private statusView (model: Model) dispatch =
    match model.State with
    | Idle
    | ReadingFile
    | Ready -> empty ()

    | Working message ->
        p {
            attr.``class`` "status"
            message
        }

    | Failed error ->
        div {
            attr.``class`` "error"
            p { "Something went wrong:" }
            pre { error }

            button {
                on.click (fun _ -> dispatch Reset)
                "Start over"
            }
        }

    | Done result when result.Links.IsEmpty && result.AlreadyLinked.IsEmpty ->
        div {
            attr.``class`` "result"
            p { "No QR codes or plain-text URLs worth linking were found." }

            button {
                on.click (fun _ -> dispatch Reset)
                "Try another"
            }
        }

    | Done result -> resultView result model.Findings dispatch

let private logView model =
    if List.isEmpty model.Log then
        empty ()
    else
        details {
            attr.``class`` "log"
            summary { "Scan log" }
            pre { String.concat "\n" model.Log }
        }

/// Position a page-1 finding's PDF point-space rect (origin bottom-left) as
/// a CSS-percentage box over the thumbnail (origin top-left) - purely from
/// the page's point-size, so it scales with however large the <img> actually
/// renders, no pixel dimensions needed.
let private overlayStyle (pageWidthPt: float, pageHeightPt: float) (f: Finding) =
    let leftPct = f.Left / pageWidthPt * 100.0
    let topPct = (pageHeightPt - f.Bottom - f.Height) / pageHeightPt * 100.0
    let widthPct = f.Width / pageWidthPt * 100.0
    let heightPct = f.Height / pageHeightPt * 100.0
    sprintf "left:%.2f%%;top:%.2f%%;width:%.2f%%;height:%.2f%%" leftPct topPct widthPct heightPct

let private findingBox (pageSize: float * float) (f: Finding) =
    div {
        attr.``class``
            (sprintf
                "finding-box %s"
                (match f.Status with
                 | Linked -> "linked"
                 | AlreadyLinked -> "already-linked"))

        attr.style (overlayStyle pageSize f)

        span {
            attr.``class`` "finding-letter"
            string f.Letter
        }
    }

/// Before Process is clicked: just the plain page-1 preview, no overlay
/// (there are no findings yet). Once a scan finishes, this switches to a
/// carousel over ResultThumbnails - one page per finding-bearing page,
/// each with its findings boxed and lettered, paged with arrows when
/// there's more than one.
let private thumbnailView (model: Model) dispatch =
    match model.State with
    | Done _ when not (List.isEmpty model.ResultThumbnails) ->
        let count = List.length model.ResultThumbnails
        let index = model.CurrentThumbnail
        let thumb = model.ResultThumbnails.[index]
        let pageFindings = model.Findings |> List.filter (fun f -> f.PageNumber = thumb.PageNumber)

        div {
            attr.``class`` "thumbnail"

            div {
                attr.``class`` "thumbnail-frame"
                img { attr.src thumb.Src }
                forEach pageFindings (findingBox thumb.PageSize)
            }

            cond (count > 1)
            <| function
                | false -> empty ()
                | true ->
                    div {
                        attr.``class`` "thumbnail-nav"

                        button {
                            attr.disabled (index = 0)
                            on.click (fun _ -> dispatch PrevResultPage)
                            "‹ Prev"
                        }

                        span { sprintf "Page %d (%d of %d)" thumb.PageNumber (index + 1) count }

                        button {
                            attr.disabled (index = count - 1)
                            on.click (fun _ -> dispatch NextResultPage)
                            "Next ›"
                        }
                    }
        }

    | Done _ -> empty ()

    | _ ->
        match model.Thumbnail with
        | None -> empty ()
        | Some(src, _) ->
            div {
                attr.``class`` "thumbnail"

                div {
                    attr.``class`` "thumbnail-frame"
                    img { attr.src src }
                }
            }

let view model dispatch =
    div {
        attr.``class`` "wrap"

        div {
            attr.``class`` "page-header"
            img {
                attr.``class`` "logo"
                attr.src "icon-192.png"
                attr.alt ""
            }
            h1 { "QR codes to clickable links" }
        }

        p {
            attr.``class`` "lede"
            "Choose a PDF, or a photo of a flyer. Every QR code whose payload is a URL, and every plain-text URL that isn't already a hyperlink, becomes a real, clickable link annotation, and you get a PDF back."
        }

        div {
            attr.``class`` "picker"

            comp<InputFile> {
                "accept" => ".pdf,application/pdf,.png,.jpg,.jpeg,.webp,.gif,.bmp,image/*"
                attr.callback<InputFileChangeEventArgs> "OnChange" (fun e -> dispatch (FileChosen e.File))
            }
        }

        div {
            attr.``class`` "options"

            label {
                input {
                    attr.``type`` "checkbox"
                    attr.``checked`` model.OcrEnabled
                    on.change (fun e -> dispatch (OcrToggled(e.Value :?> bool)))
                }

                "Also try OCR on pages with no extractable text (needed to find plain-text URLs in a photo - it has none otherwise)"
            }

            label {
                input {
                    attr.``type`` "checkbox"
                    attr.``checked`` model.BareDomainsEnabled
                    on.change (fun e -> dispatch (BareDomainsToggled(e.Value :?> bool)))
                }

                "Also link bare domains with no https:// or www. (e.g. example.com/promo)"
            }
        }

        thumbnailView model dispatch

        cond model.State
        <| function
            | Ready ->
                div {
                    attr.``class`` "actions"

                    button {
                        attr.``class`` "primary"
                        on.click (fun _ -> dispatch ProcessClicked)
                        "Process"
                    }
                }
            | _ -> empty ()

        statusView model dispatch
        logView model
    }

type QrApp() =
    inherit ProgramComponent<Model, Message>()

    [<Inject>]
    member val JS: IJSRuntime = Unchecked.defaultof<IJSRuntime> with get, set

    override this.Program =
        // IJSInProcessRuntime isn't separately registered in Blazor WASM's DI
        // container - only IJSRuntime is - but the concrete instance behind
        // it (DefaultWebAssemblyJSRuntime) implements both, so this cast is
        // safe specifically because this app only ever runs as WebAssembly.
        // Injecting IJSInProcessRuntime directly throws at component init
        // (no registered service of that type), which Blazor swallows into a
        // silent boot hang rather than a visible error - confirmed the hard
        // way.
        let jsInProcess = this.JS :?> IJSInProcessRuntime

        let loadSettings () =
            task {
                let! ocr = loadBoolSetting this.JS ocrEnabledKey
                let! bareDomains = loadBoolSetting this.JS bareDomainsEnabledKey
                return ocr, bareDomains
            }

        let init () =
            initModel,
            Cmd.batch
                [ Cmd.OfTask.either loadSettings () SettingsLoaded (fun _ -> SettingsLoaded(false, false))
                  // Bundled into startup like every other asset this app
                  // loads - not gated behind the checkbox. See the OCR
                  // fallback plan for why: this app already downloads ~27 MB
                  // of runtime unconditionally, so there's no principled
                  // reason to single out OCR's ~4-8 MB for lazy-loading.
                  Cmd.OfTask.attempt Ocr.init jsInProcess (fun _ -> Reset) ]

        Program.mkProgram (fun _ -> init ()) (update this.JS jsInProcess) view
