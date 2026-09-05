/// Finding URL-shaped text already sitting on a page - not from a QR code,
/// just plain text that was never made into a live hyperlink.
module QrLinkPdf.TextLinker

open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open iText.Kernel.Geom
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Annot
open iText.Kernel.Pdf.Canvas.Parser
open iText.Kernel.Pdf.Canvas.Parser.Data
open iText.Kernel.Pdf.Canvas.Parser.Listener

/// One text-showing operation's string and the PDF point-space rectangle
/// (origin bottom-left, same as everything else in this codebase) it was
/// drawn in.
type private Chunk = { Text: string; Rect: Rectangle; SpaceWidth: float32 }

let private rectOf (info: TextRenderInfo) =
    let baseline = info.GetBaseline()
    let ascent = info.GetAscentLine()
    let descent = info.GetDescentLine()
    let xs = [| baseline.GetStartPoint().Get(0); baseline.GetEndPoint().Get(0)
                ascent.GetStartPoint().Get(0); ascent.GetEndPoint().Get(0) |]
    let left = Array.min xs
    let right = Array.max xs
    let bottom = min (descent.GetStartPoint().Get(1)) (descent.GetEndPoint().Get(1))
    let top = max (ascent.GetStartPoint().Get(1)) (ascent.GetEndPoint().Get(1))
    Rectangle(left, bottom, right - left, top - bottom)

/// Collects every RENDER_TEXT event's string and rectangle, in the order the
/// page's content stream draws them - i.e. normal reading order for ordinary
/// flowed text, though not guaranteed for an unusual layout (see
/// `groupIntoLines`).
type private ChunkListener() =
    let chunks = ResizeArray<Chunk>()
    member _.Chunks = chunks

    interface IEventListener with
        member _.EventOccurred(data, eventType) =
            match eventType, data with
            | EventType.RENDER_TEXT, (:? TextRenderInfo as info) ->
                let text = info.GetText()

                if text.Trim() <> "" then
                    chunks.Add { Text = text; Rect = rectOf info; SpaceWidth = info.GetSingleSpaceWidth() }
            | _ -> ()

        member _.GetSupportedEvents() = HashSet([ EventType.RENDER_TEXT ]) :> ICollection<_>

/// Group chunks into lines by proximity of their vertical position, the way
/// reading a page does. This is a deliberate simplification of iText's own
/// geometric sort (its comparator lives on an internal type we can't
/// construct) - it trusts content-stream order instead. That's reliable for
/// ordinary flowed text; the worst case for an unusual, interleaved layout is
/// a missed match, never a wrong link.
let private groupIntoLines (chunks: Chunk seq) : Chunk list list =
    let sameLine (a: Chunk) (b: Chunk) =
        abs (a.Rect.GetBottom() - b.Rect.GetBottom()) < 0.5f * max (a.Rect.GetHeight()) (b.Rect.GetHeight())

    (List.ofSeq chunks, [])
    ||> List.foldBack (fun chunk lines ->
        match lines with
        | (line: Chunk list) :: rest when sameLine chunk (List.head line) -> (chunk :: line) :: rest
        | lines -> [ chunk ] :: lines)

/// Reconstruct a line's text from its chunks, inserting a space between two
/// chunks whenever the gap between them is more than half a space wide - the
/// same word-boundary heuristic iText's own extraction strategy uses - and
/// recording which span of the reconstructed string came from which chunk,
/// so a regex match can be mapped back to the rectangle(s) that drew it.
let private reconstructLine (line: Chunk list) : string * (int * int * Chunk) list =
    let text = StringBuilder()
    let spans = ResizeArray<int * int * Chunk>()
    let mutable prev = None

    for chunk in line do
        match prev with
        | Some(p: Chunk) when chunk.Rect.GetLeft() - p.Rect.GetRight() > p.SpaceWidth / 2.0f -> text.Append(' ') |> ignore
        | _ -> ()

        let start = text.Length
        text.Append(chunk.Text: string) |> ignore
        spans.Add(start, text.Length, chunk)
        prev <- Some chunk

    text.ToString(), List.ofSeq spans

let private urlPattern = Regex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Strip trailing prose punctuation ("Visit https://example.com, thanks."
/// shouldn't link the comma or the period). A trailing ')' is only trimmed
/// when it isn't balanced by a '(' earlier in the match, so
/// ".../wiki/Foo_(disambiguation)" survives intact.
let private trimTrailingPunctuation (candidate: string) =
    let closers = dict [ ')', '('; ']', '['; '}', '{' ]
    let rec trim (s: string) =
        if s = "" then
            s
        else
            let last = s.[s.Length - 1]

            let trimmable =
                match closers.TryGetValue(last) with
                | true, opener -> Seq.filter ((=) last) s |> Seq.length > (Seq.filter ((=) opener) s |> Seq.length)
                | false, _ -> ".,;:!?\"'" |> Seq.contains last

            if trimmable then trim (s.Substring(0, s.Length - 1)) else s

    trim candidate

/// Rectangles of every existing link annotation on the page, so a candidate
/// covering text that's already a live hyperlink can be skipped.
let private existingLinkRects (page: PdfPage) =
    page.GetAnnotations()
    |> Seq.choose (function
        | :? PdfLinkAnnotation as a -> Some(a.GetRectangle().ToRectangle())
        | _ -> None)
    |> List.ofSeq

/// Same "do these plausibly refer to the same spot" convention as
/// `Scanner.sameSpot` (>50% of the smaller box's area), expressed against
/// iText's `Rectangle` instead of `SKRectI` - not worth unifying the two,
/// since the rect types and origins differ.
let private overlapsExisting (existing: Rectangle list) (candidate: Rectangle) =
    let overlapsOne (r: Rectangle) =
        if not (candidate.Overlaps(r)) then
            false
        else
            let inter = candidate.GetIntersection(r)
            let overlapArea = float (inter.GetWidth() * inter.GetHeight())

            let smaller =
                min (float (candidate.GetWidth()) * float (candidate.GetHeight())) (float (r.GetWidth()) * float (r.GetHeight()))

            smaller > 0.0 && overlapArea / smaller > 0.5

    existing |> List.exists overlapsOne

/// Find every URL-shaped run of plain text on `pageNumber` that isn't
/// already a live hyperlink, and turn each into a `QrLink` ready to be
/// annotated the same way a QR code is.
let findOnPage (options: ScanOptions) (doc: PdfDocument) (pageNumber: int) : QrLink list =
    let page = doc.GetPage(pageNumber)
    let listener = ChunkListener()
    PdfCanvasProcessor(listener).ProcessPageContent(page)
    let existing = existingLinkRects page

    let found =
        listener.Chunks
        |> groupIntoLines
        |> List.collect (fun line ->
            let text, spans = reconstructLine line

            [ for m in urlPattern.Matches(text) do
                  let candidate = trimTrailingPunctuation m.Value

                  if candidate <> "" then
                      match options.UriFilter candidate with
                      | None -> ()
                      | Some uri ->
                          let matchEnd = m.Index + candidate.Length

                          let rects =
                              spans
                              |> List.choose (fun (s, e, chunk) -> if s < matchEnd && e > m.Index then Some chunk.Rect else None)

                          match rects with
                          | [] -> ()
                          | rects ->
                              let rect = Rectangle.GetCommonRectangle(Array.ofList rects)

                              yield
                                  { PageNumber = pageNumber
                                    Uri = uri
                                    Left = float (rect.GetLeft())
                                    Bottom = float (rect.GetBottom())
                                    Width = float (rect.GetWidth())
                                    Height = float (rect.GetHeight()) } ])
        |> List.filter (fun link ->
            let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)
            not (overlapsExisting existing rect))

    options.Trace(sprintf "  page %d text: found %d URL(s)" pageNumber (List.length found))
    found
