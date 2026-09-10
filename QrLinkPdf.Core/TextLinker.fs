/// Finding URL-shaped text already sitting on a page - not from a QR code,
/// just plain text that was never made into a live hyperlink.
module QrLinkPdf.TextLinker

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open PDFtoImage
open SkiaSharp
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

/// A bare host-and-path with no scheme or "www." to anchor on, e.g.
/// "qrco.de/trails-end". Only tried when `ScanOptions.MatchBareDomains` is
/// set, and only accepted afterwards if `isPlausibleDomain` agrees - the
/// shape alone ("word.word/word") also matches plenty of non-URL prose
/// (decimal figures, version numbers, section references), so the regex
/// deliberately overmatches and a second check does the real filtering.
let private bareDomainPattern =
    Regex(
        @"\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?<tld>[a-z]{2,24})(?<path>/[^\s<>""']*)",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase
    )

/// Common TLDs, deliberately not exhaustive. The point is to reject things
/// that merely have the shape of a domain - "3.14/2", "v1.2/beta" - not to
/// recognize every real domain; missing an obscure one just means a
/// bare-domain link goes unmatched, the same safe-failure shape as
/// everywhere else in this codebase. It never produces a wrong link.
let private commonTlds =
    HashSet<string>(
        [ "com"; "net"; "org"; "info"; "biz"; "name"; "pro"; "mobi"; "int"; "edu"; "gov"; "mil"
          "io"; "co"; "me"; "tv"; "cc"; "ai"; "app"; "dev"; "page"; "link"; "site"; "xyz"; "online"
          "store"; "tech"; "cloud"; "shop"; "blog"; "news"; "live"; "world"; "today"; "guide"
          "us"; "uk"; "ca"; "de"; "fr"; "es"; "it"; "nl"; "be"; "ch"; "at"; "se"; "no"; "dk"; "fi"
          "pl"; "cz"; "sk"; "hu"; "ro"; "bg"; "gr"; "pt"; "ie"; "ru"; "ua"; "tr"; "il"; "sa"; "ae"
          "cn"; "jp"; "kr"; "hk"; "tw"; "sg"; "my"; "th"; "vn"; "ph"; "in"; "id"; "au"; "nz"; "br"
          "mx"; "ar"; "cl"; "pe"; "za"; "ng"; "ke"; "eg"; "ly"; "gl"; "fm"; "im"; "to"; "sh"; "gg" ],
        StringComparer.OrdinalIgnoreCase
    )

/// Does a bare-domain-shaped match look like an actual domain? Its TLD has
/// to be a real one, and the host has to contain a letter somewhere - a
/// version number or decimal figure followed by a slash ("3.14/2") only
/// clears both checks by coincidence, which is rare enough to accept.
let private isPlausibleDomain (m: Match) =
    let tld = m.Groups.["tld"].Value
    let host = m.Value.Substring(0, m.Value.Length - m.Groups.["path"].Value.Length)
    commonTlds.Contains(tld) && Regex.IsMatch(host, "[a-zA-Z]")

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

/// Real text and OCR can disagree noticeably on a word's exact vertical
/// extent for the same visible line - real chunks use font-metric ascent/
/// descent, OCR reports the tight ink bounding box of what it actually
/// recognized - close enough in practice to land a line-height apart with
/// zero literal rectangle overlap, confirmed against a real document. So
/// cross-source duplicate detection uses a looser rule than `sameSpot`:
/// significant horizontal overlap, and vertical centres within one line
/// height of each other.
let private nearSameSpot (a: Rectangle) (b: Rectangle) =
    let left = max (a.GetLeft()) (b.GetLeft())
    let right = min (a.GetRight()) (b.GetRight())
    let horizontalOverlap = max 0f (right - left)
    let narrower = min (a.GetWidth()) (b.GetWidth())

    let centreY (r: Rectangle) = r.GetBottom() + r.GetHeight() / 2f
    let lineHeight = max (a.GetHeight()) (b.GetHeight())

    narrower > 0f
    && horizontalOverlap / narrower > 0.5f
    && abs (centreY a - centreY b) < lineHeight

/// Real text and OCR can both independently find the same visible text -
/// OCR has no idea a region was already covered by a real text chunk, and
/// `findOnPage` runs it unconditionally (see its doc comment), so the same
/// spot can come back twice: once from each source. Collapse candidates
/// that occupy the same spot, keeping the first - real-text chunks are
/// listed before OCR chunks in `findOnPage`, so this prefers the more
/// precisely-positioned real-text rect whenever both exist.
///
/// Same spot is enough on its own, deliberately not also requiring a
/// matching URI: confirmed against a real document where OCR misread a
/// printed URL ("wlf" as "wif") at the exact spot real text had already
/// extracted correctly, and the differing URI let a wrong extra link
/// through. Real text is authoritative wherever it exists at all - OCR
/// exists to cover pages that have none (findOnPage's own doc comment) -
/// so a same-spot OCR candidate should lose even when it disagrees on what
/// the text says, not just when it agrees.
let private dedupeBySpot (links: QrLink list) : QrLink list =
    let kept = ResizeArray<QrLink>()

    for link in links do
        let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)

        let isDuplicate =
            kept
            |> Seq.exists (fun k -> nearSameSpot rect (Rectangle(float32 k.Left, float32 k.Bottom, float32 k.Width, float32 k.Height)))

        if not isDuplicate then
            kept.Add link

    List.ofSeq kept

/// Find every URL-shaped run in `chunks`, split into those that aren't
/// already a live hyperlink (ready to be annotated the same way a QR code
/// is) and those that are (found, but left alone). Doesn't care whether the
/// chunks came from real text-showing operators or from an OCR engine - both
/// are just positioned runs of text by the time they get here.
let private linkChunks
    (options: ScanOptions)
    (pageNumber: int)
    (existing: Rectangle list)
    (chunks: Chunk list)
    : QrLink list * QrLink list =
    let found =
        chunks
        |> groupIntoLines
        |> List.collect (fun line ->
            let text, spans = reconstructLine line
            let schemeMatches = urlPattern.Matches(text) |> Seq.cast<Match> |> List.ofSeq

            let overlapsScheme (m: Match) =
                schemeMatches
                |> List.exists (fun s -> m.Index < s.Index + s.Length && m.Index + m.Length > s.Index)

            // Bare-domain matches that land on text a scheme match already
            // covers are dropped rather than double-counted - matters when
            // e.g. "https://qrco.de/trails-end" would otherwise also satisfy
            // the bare-domain shape on its "qrco.de/trails-end" tail.
            let bareDomainMatches =
                if options.MatchBareDomains then
                    bareDomainPattern.Matches(text)
                    |> Seq.cast<Match>
                    |> Seq.filter (fun m -> not (overlapsScheme m) && isPlausibleDomain m)
                    |> List.ofSeq
                else
                    []

            // A scheme match's own text is already a valid absolute URI (or
            // "www."-prefixed) for UriFilter to judge; a bare-domain match
            // isn't, so it needs "https://" added before UriFilter sees it.
            let candidates =
                (schemeMatches |> List.map (fun m -> m, false))
                @ (bareDomainMatches |> List.map (fun m -> m, true))

            [ for (m, needsScheme) in candidates do
                  let trimmed = trimTrailingPunctuation m.Value
                  let filterInput = if needsScheme then "https://" + trimmed else trimmed

                  if trimmed <> "" then
                      match options.UriFilter filterInput with
                      | None -> ()
                      | Some uri ->
                          let matchEnd = m.Index + trimmed.Length

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

    // Dedupe real-text-vs-OCR duplicates *before* checking against existing
    // annotations, not after: OCR's bounding box for the same visible text
    // can land just far enough from a real-text chunk's more precise one
    // that only one of the two clears the >50% overlap threshold against an
    // existing annotation. Partitioning first would then split the same
    // URL's two candidates across both buckets - the one that missed the
    // overlap check surviving into `linked` and getting a genuinely
    // duplicate annotation added right on top of the live one. Deduping
    // first collapses them into a single candidate (preferring the
    // real-text one - see dedupeBySpot) before that check ever runs, so
    // there's only ever one fate to land in.
    dedupeBySpot found
    |> List.partition (fun link ->
        let rect = Rectangle(float32 link.Left, float32 link.Bottom, float32 link.Width, float32 link.Height)
        not (ExistingLinks.overlapsAny existing rect))

/// Tesseract's LSTM models are trained around roughly this DPI-equivalent
/// glyph size; feeding it much higher-resolution text doesn't help the way
/// it helps QR detection - it can actively hurt recognition (confirmed
/// against a real document: options.Dpi = 400 misread "wlf" as "wif" at
/// the exact spot options.Dpi = 300 read correctly). options.Dpi is tuned
/// for finding small/dense QR codes (see ScanOptions.Default's own
/// comment) - a different, unrelated optimum - so OCR gets its own fixed
/// rasterization DPI here instead of inheriting that knob.
let private ocrDpi = 300

/// Rasterize just this one page - re-rasterizing here (rather than
/// threading the QR path's whole-PDF rasterization pass through) keeps
/// this module independent of PdfQrLinker's.
let private rasterizeOcrPage (pdfBytes: byte[]) (pageNumber: int) : SKBitmap =
    let renderOptions = RenderOptions(Dpi = ocrDpi, WithAnnotations = false, WithFormFill = true)
    Conversion.ToImage(pdfBytes, Index(pageNumber - 1), options = renderOptions)

/// Runs `engine` over `bitmap` and converts each OCR'd word into a `Chunk`
/// in PDF point-space, so it can go through the exact same
/// line-reconstruction/regex/UriFilter pipeline real text chunks do.
/// `bitmap` may be a crop of the full rasterized page (see
/// ocrChunksTiled) - `yOffsetPx` shifts each word's box back into the full
/// page's own pixel space before Geometry.pixelBoxToPoint (which needs
/// `fullBitmapSize`, not the crop's own size) converts it.
let private wordsFromBitmap
    (engine: SKBitmap -> OcrWord list)
    (pageSize: float * float)
    (fullBitmapSize: int * int)
    (yOffsetPx: int)
    (bitmap: SKBitmap)
    : Chunk list =
    engine bitmap
    |> List.map (fun word ->
        let shiftedBox = SKRectI(word.Box.Left, word.Box.Top + yOffsetPx, word.Box.Right, word.Box.Bottom + yOffsetPx)
        let box = Geometry.pixelBoxToPoint pageSize fullBitmapSize shiftedBox
        let rect = Rectangle(float32 box.Left, float32 box.Bottom, float32 box.Width, float32 box.Height)
        // OCR gives whole words, not sub-word glyph runs, so a modest
        // fraction of the word's own height is a reasonable stand-in for
        // "space width" - reconstructLine only uses it to decide whether to
        // insert a space between adjacent chunks, and adjacent OCR words
        // almost always need one.
        { Text = word.Text; Rect = rect; SpaceWidth = rect.GetHeight() * 0.3f })

/// How many overlapping horizontal strips ocrChunksTiled splits a page
/// into, and by how much each strip overlaps its neighbor - enough that a
/// text line straddling a tile boundary still lands fully inside at least
/// one tile. Not hand-tuned to any one document: tested at 4/6/8 strips
/// against the real flyer that motivated this, all three found the URL a
/// whole-page pass missed.
let private tileCount = 6
let private tileOverlapFraction = 0.5

/// Splits `bitmap` into `tileCount` overlapping horizontal strips and OCRs
/// each independently - the fallback for when a single whole-page pass
/// finds nothing linkable (see findOnPage). Confirmed against a real
/// flyer: Tesseract's own layout analysis can silently skip a text region
/// entirely on a busy graphic page - not a recognition problem (the exact
/// same pixels, cropped down to roughly this size, read perfectly), a
/// segmentation one. Feeding it a smaller, more homogeneous region at a
/// time is what actually fixes that, unlike a color/contrast preprocessing
/// pass, which risks corrupting text that was already reading correctly
/// (confirmed: a full-page grayscale + high-contrast pass also found the
/// same URL, but also flipped one already-correct word - "DR," into
/// "Dk," - since it alters every pixel; tiling never does, only which
/// sub-region Tesseract sees at once).
let private ocrChunksTiled (engine: SKBitmap -> OcrWord list) (pageSize: float * float) (bitmap: SKBitmap) : Chunk list =
    let fullSize = bitmap.Width, bitmap.Height
    let stripHeight = bitmap.Height / tileCount
    let step = max 1 (int (float stripHeight * (1.0 - tileOverlapFraction)))

    [ let mutable y = 0

      while y < bitmap.Height do
          let h = min stripHeight (bitmap.Height - y)

          if h > 0 then
              use strip = new SKBitmap(bitmap.Width, h)
              use canvas = new SKCanvas(strip)

              canvas.DrawBitmap(
                  bitmap,
                  SKRect(0f, float32 y, float32 bitmap.Width, float32 (y + h)),
                  SKRect(0f, 0f, float32 bitmap.Width, float32 h),
                  SKSamplingOptions()
              )

              yield! wordsFromBitmap engine pageSize fullSize y strip

          y <- y + step ]

/// Find every URL-shaped run of plain text on `pageNumber` that isn't
/// already a live hyperlink, and turn each into a `QrLink` ready to be
/// annotated the same way a QR code is. Also tries OCR (if
/// `options.OcrEngine` is set) and merges its results in, unconditionally -
/// not just when the page has zero real text. A page can have *some* real
/// text (page numbers, a heading) while its body copy is flattened to
/// vector outlines elsewhere on the same page, invisible to text extraction
/// no matter how it's done; a "run OCR only if nothing else was found"
/// trigger misses exactly that case, which is common enough in practice
/// (confirmed against a real flyer) that it isn't worth the false economy.
let findOnPage (options: ScanOptions) (pdfBytes: byte[]) (doc: PdfDocument) (pageNumber: int) : QrLink list * QrLink list =
    let page = doc.GetPage(pageNumber)
    let listener = ChunkListener()
    PdfCanvasProcessor(listener).ProcessPageContent(page)
    let existing = ExistingLinks.rects page
    let realChunks = List.ofSeq listener.Chunks

    let ocrChunksFound =
        match options.OcrEngine with
        | Some engine ->
            options.Trace(sprintf "  page %d: trying OCR" pageNumber)
            let size = page.GetPageSize()
            let pageSize = float (size.GetWidth()), float (size.GetHeight())
            use bitmap = rasterizeOcrPage pdfBytes pageNumber
            let wholeChunks = wordsFromBitmap engine pageSize (bitmap.Width, bitmap.Height) 0 bitmap

            // Cheap probe: did the whole-page pass alone find anything
            // linkable? findOnPage already runs OCR unconditionally on
            // every page (see this function's own comment above) - paying
            // for the much more expensive tiled fallback on every page
            // too, regardless of whether it was ever needed, would
            // compound badly across a multi-page PDF. Only escalate when
            // the cheap pass came up empty, and only on the actual thing
            // that matters (a URL-shaped candidate), not a raw word-count
            // heuristic.
            let foundSomething =
                let linked, alreadyLinked = linkChunks options pageNumber existing wholeChunks
                not (List.isEmpty linked && List.isEmpty alreadyLinked)

            if foundSomething then
                wholeChunks
            else
                options.Trace(sprintf "  page %d: OCR found nothing linkable, retrying tiled" pageNumber)
                wholeChunks @ ocrChunksTiled engine pageSize bitmap
        | None -> []

    let chunks = realChunks @ ocrChunksFound

    let linked, alreadyLinked = linkChunks options pageNumber existing chunks
    options.Trace(sprintf "  page %d text: found %d URL(s)" pageNumber (List.length linked))

    for l in alreadyLinked do
        options.Trace(sprintf "  page %d text: %s already linked, skipping" pageNumber l.Uri)

    linked, alreadyLinked
