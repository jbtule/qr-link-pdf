// Browser-side helpers the Blazor app calls through JS interop.
window.qrLinkPdf = {
  // Hands the linked PDF to the browser as a download. Takes a Blazor
  // DotNetStreamReference rather than a base64 data: URL, so the bytes cross
  // the interop boundary once instead of being encoded, copied and decoded.
  download: async function (fileName, streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: 'application/pdf' }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  },

  // Resolves once the browser has actually painted whatever the app just
  // rendered. The scan runs synchronously on the one and only thread, so
  // a "page 2 of 5" status only becomes visible if the app awaits this
  // between pages - a bare Task.Yield isn't enough, since it can resume
  // before the event loop ever gets a rendering opportunity.
  // requestAnimationFrame fires just *before* the next paint; the
  // setTimeout(0) inside it lands just *after*.
  nextPaint: function () {
    return new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0)));
  }
};
