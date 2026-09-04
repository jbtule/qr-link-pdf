// Boots the WebAssembly build under node and forwards its exit code.
// dotnet.js detects node and loads the runtime itself, so this needs no
// browser and no web server - the same trick Microsoft's own
// Microsoft.Testing.Platform browser sample uses.
import { dotnet } from './_framework/dotnet.js';

const { runMain } = await dotnet
    .withApplicationArguments(...process.argv.slice(2))
    .create();

process.exitCode = await runMain();
