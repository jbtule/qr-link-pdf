namespace QrLinkPdf

open System

/// A QR code located on a page, positioned in PDF user-space points with the
/// origin at the bottom-left of the page - i.e. ready to drop straight into a
/// link annotation.
type QrLink =
    { /// 1-based page number.
      PageNumber: int
      /// The QR payload, after the scan's URI filter has accepted (and
      /// possibly rewritten) it.
      Uri: string
      Left: float
      Bottom: float
      Width: float
      Height: float }

    member this.Right = this.Left + this.Width
    member this.Top = this.Bottom + this.Height

/// Tuning knobs for a scan. Start from `ScanOptions.Default` and override
/// what you need.
type ScanOptions =
    { /// DPI used to rasterize each page for scanning. Higher = better
      /// detection of small/dense codes, slower and more memory.
      Dpi: int
      /// Relative sizes at which each rasterized page is decoded; results
      /// from every level are merged. See `Scanner.findOnBitmap` for why a
      /// single pass isn't enough.
      Scales: float list
      /// Decides whether a decoded payload is worth linking, and what URI to
      /// link it to. Return `None` to skip the code.
      UriFilter: string -> string option
      /// Called with human-readable progress/diagnostic lines. Defaults to
      /// discarding them.
      Trace: string -> unit }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScanOptions =

    /// Does the decoded QR payload look like something worth hyperlinking?
    /// iText's PdfAction.CreateURI just needs an absolute URI; we also allow
    /// bare "www." text and upgrade it to https.
    let defaultUriFilter (text: string) : string option =
        let text = text.Trim()

        if Uri.IsWellFormedUriString(text, UriKind.Absolute) then
            Some text
        elif text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) then
            Some("https://" + text)
        else
            None

    /// Sensible defaults: 400 DPI, a four-level scan pyramid, absolute-URI
    /// payloads only, and no tracing.
    let Default =
        { Dpi = 400
          Scales = [ 1.0; 0.6; 0.4; 0.25 ]
          UriFilter = defaultUriFilter
          Trace = ignore }

    /// Tuned for somebody waiting on the result - about half the work of
    /// `Default` for the same detection on the fixtures in the test suite.
    ///
    /// The dropped resolution is what saves the time; the scan pyramid is
    /// kept nearly intact on purpose. Trimming it to [1.0; 0.5] is tempting
    /// and measurably wrong: it stops finding low-contrast codes, because the
    /// smallest level is doing the work described in `Scanner.findOnBitmap`.
    let Interactive =
        { Default with
            Dpi = 300
            Scales = [ 1.0; 0.5; 0.25 ] }
