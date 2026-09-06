/// Converting a rasterized page bitmap's pixel space (origin top-left) into
/// a PDF page's point space (origin bottom-left). Shared by the QR path
/// (PdfQrLinker, from ZXing's boxes) and the OCR path (TextLinker, from an
/// OCR engine's word boxes) - both start from the same kind of rasterized
/// bitmap and need the same conversion.
module QrLinkPdf.Geometry

open SkiaSharp

/// A box in PDF point-space, origin bottom-left - the same shape QrLink's
/// own fields use, without committing to a page number or URI yet.
type PointBox =
    { Left: float
      Bottom: float
      Width: float
      Height: float }

/// Convert a pixel-space box on a bitmap rendered at `bitmapWidth x
/// bitmapHeight` into PDF point-space for a page sized `pageWidthPt x
/// pageHeightPt`. Image Y grows downward from the top; PDF Y grows upward
/// from the bottom, so this flips it.
let pixelBoxToPoint
    (pageWidthPt: float, pageHeightPt: float)
    (bitmapWidth: int, bitmapHeight: int)
    (box: SKRectI)
    : PointBox =
    let scaleX = pageWidthPt / float bitmapWidth
    let scaleY = pageHeightPt / float bitmapHeight
    let left = float box.Left * scaleX
    let right = float box.Right * scaleX
    let top = pageHeightPt - float box.Top * scaleY
    let bottom = pageHeightPt - float box.Bottom * scaleY

    { Left = left
      Bottom = bottom
      Width = right - left
      Height = top - bottom }
