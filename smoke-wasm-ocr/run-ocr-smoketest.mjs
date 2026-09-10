// Serves a published QrLinkPdf.Wasm app and drives its real UI headlessly
// via Playwright: upload fixture.pdf, enable the OCR checkbox, click
// Process, and check two things - real OCR actually found the URL, and
// Blazor's global #blazor-error-ui banner never showed up.
//
// That second check is the whole reason this file exists and isn't just a
// "did it build" smoke test: Blazor WebAssembly's runtime wires its err:
// callback (blazor.webassembly.js) to show that banner on *any* native
// stderr write, unconditionally - a real, previously-shipped regression in
// jbtule/tesseract-nuget-platforms' browser-wasm preview builds left a real,
// successful OCR scan sitting under a "something went wrong" overlay (see
// the "Move Tesseract into Core; bump to the fixed preview build" commit).
// A console-text-only check would have missed that entirely - confirmed by
// running this exact check against the unfixed build first, before it was
// fixed upstream: it correctly failed.
//
// Usage: node run-ocr-smoketest.mjs <path-to-published-wwwroot>
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { join, extname, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const wwwroot = process.argv[2];
if (!wwwroot) {
  console.error('Usage: node run-ocr-smoketest.mjs <path-to-published-wwwroot>');
  process.exit(2);
}

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturePdf = join(__dirname, 'fixture.pdf');
const expectedUrl = 'https://example.com/wasm-ocr-smoke';

const PORT = 8935;
const CONTENT_TYPES = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.wasm': 'application/wasm', '.json': 'application/json',
  '.css': 'text/css', '.ico': 'image/x-icon', '.png': 'image/png',
  '.webmanifest': 'application/manifest+json',
  '.dat': 'application/octet-stream', '.dll': 'application/octet-stream',
  '.blat': 'application/octet-stream', '.br': 'application/octet-stream',
  '.gz': 'application/octet-stream', '.traineddata': 'application/octet-stream',
};

const server = createServer(async (req, res) => {
  try {
    let urlPath = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    if (urlPath === '/') urlPath = '/index.html';
    const filePath = join(wwwroot, urlPath);
    const st = await stat(filePath);
    if (!st.isFile()) throw new Error('not a file');
    const body = await readFile(filePath);
    res.setHeader('Content-Type', CONTENT_TYPES[extname(filePath)] ?? 'application/octet-stream');
    res.writeHead(200);
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end('not found');
  }
});

await new Promise((resolve) => server.listen(PORT, resolve));
console.log(`Serving ${wwwroot} on http://localhost:${PORT}/`);

const browser = await chromium.launch();
const page = await browser.newPage();
page.on('console', (msg) => console.log('[console]', msg.text()));
page.on('pageerror', (err) => console.log('[pageerror]', err.message));

let ok = false;
let errorUiVisible = false;
let pageText = '';
try {
  await page.goto(`http://localhost:${PORT}/index.html`, { waitUntil: 'load', timeout: 60000 });
  await page.waitForSelector('input[type=file]', { timeout: 60000 });

  // Same startup Ocr.init load every real app boot does (fetches
  // tessdata/eng.traineddata + constructs TesseractEngine) - give it time
  // to finish alongside the rest of the runtime boot before using the
  // checkbox.
  await page.waitForTimeout(3000);

  const ocrCheckbox = page.locator('.options label').first().locator('input[type=checkbox]');
  await ocrCheckbox.check();
  await page.waitForTimeout(500); // OcrToggled dispatches Reset; wait for the re-render.

  await page.setInputFiles('input[type=file]', fixturePdf);
  await page.waitForTimeout(1500); // FileChosen -> ReadingFile -> Ready.

  await page.click('button.primary');

  await page.waitForFunction(
    () =>
      document.body.innerText.includes('Found') ||
      document.body.innerText.includes('No QR codes') ||
      document.body.innerText.includes('Something went wrong:'),
    { timeout: 60000 }
  );

  pageText = await page.locator('body').innerText();
  const foundLink = await page.locator(`a[href="${expectedUrl}"]`).count();

  errorUiVisible = await page.evaluate(() => {
    const el = document.querySelector('#blazor-error-ui');
    return !!el && getComputedStyle(el).display !== 'none';
  });

  ok = foundLink > 0;
} finally {
  await browser.close();
  server.close();
}

console.log('--- page text ---');
console.log(pageText);

if (!ok) {
  console.error(`FAILURE: OCR did not find a link to ${expectedUrl}.`);
  process.exit(1);
}

if (errorUiVisible) {
  console.error('FAILURE: #blazor-error-ui is visible - some native stderr write triggered it during a run that otherwise completed successfully.');
  process.exit(1);
}

console.log('SUCCESS');
process.exit(0);
