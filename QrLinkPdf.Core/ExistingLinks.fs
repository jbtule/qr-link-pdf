/// Shared "is this spot already a live hyperlink?" check, used by both the
/// QR path and the text path so a freshly found candidate that overlaps an
/// existing `PdfLinkAnnotation` is skipped consistently either way.
module QrLinkPdf.ExistingLinks

open iText.Kernel.Geom
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Annot

/// Rectangles of every existing link annotation on a page, so a freshly
/// found candidate that already has a live hyperlink can be skipped.
let rects (page: PdfPage) : Rectangle list =
    page.GetAnnotations()
    |> Seq.choose (function
        | :? PdfLinkAnnotation as a -> Some(a.GetRectangle().ToRectangle())
        | _ -> None)
    |> List.ofSeq

/// Same "do these plausibly refer to the same spot" convention as
/// `Scanner.sameSpot` (>50% of the smaller box's area), expressed against
/// iText's `Rectangle` instead of `SKRectI` - not worth unifying the two,
/// since the rect types and origins differ.
let private sameSpot (a: Rectangle) (b: Rectangle) =
    if not (a.Overlaps(b)) then
        false
    else
        let inter = a.GetIntersection(b)
        let overlapArea = float (inter.GetWidth() * inter.GetHeight())
        let smaller = min (float (a.GetWidth()) * float (a.GetHeight())) (float (b.GetWidth()) * float (b.GetHeight()))
        smaller > 0.0 && overlapArea / smaller > 0.5

let overlapsAny (existing: Rectangle list) (candidate: Rectangle) =
    existing |> List.exists (sameSpot candidate)
