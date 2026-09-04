// Hands the linked PDF to the browser as a download. Takes a Blazor
// DotNetStreamReference rather than a base64 data: URL, so the bytes cross the
// interop boundary once instead of being encoded, copied and decoded.
window.qrLinkPdf = {
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
  }
};
