// Bridges to tesseract-wasm's low-level, synchronous OCREngine (not the
// high-level OCRClient, which runs in a Worker and is Promise-based - the
// whole point here is a synchronous call PdfQrLinker.link's existing
// synchronous pipeline can use directly, via IJSInProcessRuntime).
//
// Loading the engine and its trained-data model is the one unavoidably
// async step (fetching the WASM module and ~4 MB of model data); recognize()
// itself is plain and synchronous once that's done.
window.qrLinkPdfOcr = {
  engine: null,

  async init() {
    if (this.engine) {
      return;
    }

    // Same asset-host convention index.html's loadBootResource hook uses -
    // empty means same origin (local dev), set means the Cloudflare asset
    // host (deployed).
    const base = window.qrLinkPdfAssetBase || './';

    const { createOCREngine } = await import(base + 'tesseract-wasm/lib.js');
    this.engine = await createOCREngine();

    const modelResponse = await fetch(base + 'tessdata/eng.traineddata');
    const modelBytes = await modelResponse.arrayBuffer();
    this.engine.loadModel(new Uint8Array(modelBytes));
  },

  // `bytes` is a flat RGBA8888 pixel buffer - the .NET side has already done
  // any colour-space conversion, since that's cheap and unambiguous there,
  // and OCREngine.loadImage just wants {width, height, data} shaped like
  // ImageData, not an actual ImageData instance.
  recognize(bytes, width, height) {
    // The engine is loaded eagerly at app startup (see Main.fs) and again,
    // as a no-op safety net, right before a scan that wants it - but if it
    // genuinely never loaded (a network hiccup, say), fail this one scan's
    // OCR quietly rather than throwing on a null engine.
    if (!this.engine) {
      return [];
    }

    const data = new Uint8ClampedArray(bytes);
    this.engine.loadImage({ width, height, data });

    const words = this.engine.getTextBoxes('word');
    this.engine.clearImage();

    return words.map((w) => ({
      left: w.rect.left,
      top: w.rect.top,
      right: w.rect.right,
      bottom: w.rect.bottom,
      text: w.text,
    }));
  },
};
