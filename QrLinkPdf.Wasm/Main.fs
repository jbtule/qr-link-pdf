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
    | Done of QrLink list
    | Failed of string

type Model =
    { State: State
      FileName: string
      FileBytes: byte[] option
      /// data:image/png;base64,... of the PDF's first page, or None if
      /// rendering it failed - never blocks choosing/processing the file.
      Thumbnail: string option
      Output: byte[] option
      Log: string list
      OcrEnabled: bool
      BareDomainsEnabled: bool }

let initModel =
    { State = Idle
      FileName = ""
      FileBytes = None
      Thumbnail = None
      Output = None
      Log = []
      OcrEnabled = false
      BareDomainsEnabled = false }

type Message =
    | FileChosen of IBrowserFile
    | Loaded of name: string * bytes: byte[] * thumbnail: string option
    | ProcessClicked
    | Finished of output: byte[] * links: QrLink list * log: string list
    | Errored of exn
    | Download
    | Reset
    | OcrToggled of bool
    | BareDomainsToggled of bool
    | SettingsLoaded of ocr: bool * bareDomains: bool

/// Renders just the first page, small enough to be cheap but large enough to
/// actually read once the CSS caps its display width. Failure (a corrupt
/// PDF, zero pages) degrades to no thumbnail rather than blocking anything.
let private tryRenderThumbnail (bytes: byte[]) : string option =
    try
        use bitmap =
            Conversion.ToImage(bytes, Index(0), options = RenderOptions(Dpi = 100, WithAnnotations = false, WithFormFill = true))

        use image = SKImage.FromBitmap(bitmap)
        use data = image.Encode(SKEncodedImageFormat.Png, 100)
        Some("data:image/png;base64," + Convert.ToBase64String(data.ToArray()))
    with _ ->
        None

/// The browser's file stream only supports async reads, and QrLinkPdf.Core
/// reads synchronously - so buffer here first. It costs nothing: Core's
/// readAll short-circuits on a MemoryStream.
let private readFileAndThumbnail (file: IBrowserFile) =
    task {
        use source = file.OpenReadStream(maxAllowedSize = maxUploadBytes)
        let buffer = new MemoryStream()
        do! source.CopyToAsync(buffer)
        let bytes = buffer.ToArray()
        // Let the "Reading..." render land before rendering the thumbnail
        // blocks the only thread.
        do! Task.Yield()
        return file.Name, bytes, tryRenderThumbnail bytes
    }

let private processFile
    (js: IJSRuntime)
    (jsInProcess: IJSInProcessRuntime)
    (ocrEnabled: bool)
    (bareDomains: bool)
    (bytes: byte[])
    =
    task {
        do! Task.Yield()

        if ocrEnabled then
            // A no-op if the startup load (see QrApp.Program) already
            // succeeded - this only does real work in the rare case a scan
            // starts before that finishes, or if it never does. Either way
            // a failure here just means this scan runs without OCR; ocr.js
            // itself returns no words rather than throwing on a still-null
            // engine.
            try
                do! Ocr.init js
            with _ ->
                ()

        let log = ResizeArray<string>()
        let ocrEngine = if ocrEnabled then Some(Ocr.create jsInProcess) else None
        use input = new MemoryStream(bytes)
        use output = new MemoryStream()
        let links = PdfQrLinker.link (browserOptions log.Add ocrEngine bareDomains) input output
        // link's PdfDocument is disposed by the time it returns, so the bytes
        // are complete here.
        return output.ToArray(), links, List.ofSeq log
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
            Cmd.OfTask.either (processFile js jsInProcess model.OcrEnabled model.BareDomainsEnabled) bytes Finished Errored

    | Finished(output, links, log) ->
        { model with
            State = Done links
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

let private resultView links dispatch =
    div {
        attr.``class`` "result"

        p {
            attr.``class`` "count"
            sprintf "Found %d clickable link%s to add." (List.length links) (if List.length links = 1 then "" else "s")
        }

        ul {
            attr.``class`` "links"

            forEach links (fun link ->
                li {
                    span {
                        attr.``class`` "page"
                        sprintf "Page %d" link.PageNumber
                    }

                    a {
                        attr.href link.Uri
                        attr.target "_blank"
                        link.Uri
                    }
                })
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

let private statusView model dispatch =
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

    | Done [] ->
        div {
            attr.``class`` "result"
            p { "No QR codes or plain-text URLs worth linking were found." }

            button {
                on.click (fun _ -> dispatch Reset)
                "Try another"
            }
        }

    | Done links -> resultView links dispatch

let private logView model =
    if List.isEmpty model.Log then
        empty ()
    else
        details {
            attr.``class`` "log"
            summary { "Scan log" }
            pre { String.concat "\n" model.Log }
        }

let private thumbnailView model =
    match model.Thumbnail with
    | None -> empty ()
    | Some src ->
        div {
            attr.``class`` "thumbnail"
            img { attr.src src }
        }

let view model dispatch =
    div {
        attr.``class`` "wrap"
        h1 { "QR codes to clickable links" }

        p {
            attr.``class`` "lede"
            "Choose a PDF. Every QR code whose payload is a URL, and every plain-text URL that isn't already a hyperlink, becomes a real, clickable link annotation, and you get the PDF back."
        }

        div {
            attr.``class`` "picker"

            comp<InputFile> {
                "accept" => ".pdf,application/pdf"
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

                "Also try OCR on pages with no extractable text"
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

        thumbnailView model

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
                  Cmd.OfTask.attempt Ocr.init this.JS (fun _ -> Reset) ]

        Program.mkProgram (fun _ -> init ()) (update this.JS jsInProcess) view
