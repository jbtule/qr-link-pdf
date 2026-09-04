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
open QrLinkPdf

/// IBrowserFile.OpenReadStream defaults to a 512 KB cap, which any real PDF
/// blows straight through.
let private maxUploadBytes = 64L * 1024L * 1024L

/// ZXing decodes interpreted in the browser, so use the lighter scan.
let private browserOptions trace = { ScanOptions.Interactive with Trace = trace }

type State =
    | Idle
    | Working of string
    | Done of QrLink list
    | Failed of string

type Model =
    { State: State
      FileName: string
      Output: byte[] option
      Log: string list }

let initModel =
    { State = Idle
      FileName = ""
      Output = None
      Log = [] }

type Message =
    | FileChosen of IBrowserFile
    | Read of name: string * bytes: byte[]
    | Finished of output: byte[] * links: QrLink list * log: string list
    | Errored of exn
    | Download
    | Reset

/// The browser's file stream only supports async reads, and QrLinkPdf.Core
/// reads synchronously - so buffer here first. It costs nothing: Core's
/// readAll short-circuits on a MemoryStream.
let private readFile (file: IBrowserFile) =
    task {
        use source = file.OpenReadStream(maxAllowedSize = maxUploadBytes)
        let buffer = new MemoryStream()
        do! source.CopyToAsync(buffer)
        return file.Name, buffer.ToArray()
    }

let private runScan (bytes: byte[]) =
    task {
        // Let the "Scanning..." render land before we block the only thread.
        do! Task.Yield()

        let log = ResizeArray<string>()
        use input = new MemoryStream(bytes)
        use output = new MemoryStream()
        let links = PdfQrLinker.link (browserOptions log.Add) input output
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

let update (js: IJSRuntime) message model =
    match message with
    | FileChosen file ->
        { model with
            State = Working "Reading the file..."
            FileName = file.Name
            Output = None
            Log = [] },
        Cmd.OfTask.either readFile file Read Errored

    | Read(name, bytes) ->
        { model with State = Working(sprintf "Scanning %s..." name) }, Cmd.OfTask.either runScan bytes Finished Errored

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

    | Reset -> initModel, Cmd.none

let private resultView links dispatch =
    div {
        attr.``class`` "result"

        p {
            attr.``class`` "count"
            sprintf "Found %d linkable QR code%s." (List.length links) (if List.length links = 1 then "" else "s")
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
    | Idle -> empty ()

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
            p { "No QR codes with URL payloads were found." }

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

let view model dispatch =
    div {
        attr.``class`` "wrap"
        h1 { "QR codes to clickable links" }

        p {
            attr.``class`` "lede"
            "Choose a PDF. Every QR code whose payload is a URL becomes a real, clickable link annotation over the code, and you get the PDF back."
        }

        div {
            attr.``class`` "picker"

            comp<InputFile> {
                "accept" => ".pdf,application/pdf"
                attr.callback<InputFileChangeEventArgs> "OnChange" (fun e -> dispatch (FileChosen e.File))
            }
        }

        statusView model dispatch
        logView model
    }

type QrApp() =
    inherit ProgramComponent<Model, Message>()

    [<Inject>]
    member val JS: IJSRuntime = Unchecked.defaultof<IJSRuntime> with get, set

    override this.Program =
        Program.mkProgram (fun _ -> initModel, Cmd.none) (update this.JS) view
