// Boots the WebAssembly build under node/bun and forwards its exit code -
// same trick QrLinkPdf.Wasm.SmokeTest's own runtests.mjs uses (dotnet.js
// detects a non-browser JS host and loads the runtime itself, no browser
// or web server needed). Confirmed working identically under both node
// and bun.
import { dotnet } from './_framework/dotnet.js';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const dotnetInstance = await dotnet
    .withApplicationArguments(...process.argv.slice(2))
    .create();

// OcrTests.fs's real TesseractEngine reads tessdata/eng.traineddata via
// plain POSIX File.Exists/File.OpenRead calls, which Mono routes to its
// own Emscripten-backed virtual filesystem - NOT through fetch() at all
// (confirmed: intercepting global.fetch to serve wwwroot/ files from
// disk had no effect and even hung the runtime; _framework/* assets are
// loaded through a node/bun-specific path inside dotnet.js that never
// touches fetch() either). So the file has to be written directly into
// that virtual FS via the Module object create() exposes, before
// runMain() - same repo-root tessdata/ this project's own tests already
// need locally (see QrLinkPdf.Tests.fsproj's own comment; CI fetches it
// fresh, see deploy.yml), read from its real path on disk since nothing
// here ever publishes it as a static web asset.
const tessdataPath = path.join(__dirname, '../../../../../tessdata/eng.traineddata');
if (existsSync(tessdataPath)) {
    const bytes = readFileSync(tessdataPath);
    try {
        dotnetInstance.Module.FS.mkdir('/tessdata');
    } catch {
        // already exists
    }
    dotnetInstance.Module.FS.writeFile('/tessdata/eng.traineddata', bytes);
}

process.exitCode = await dotnetInstance.runMain();
