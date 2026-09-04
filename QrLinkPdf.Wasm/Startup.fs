module QrLinkPdf.Wasm.Startup

open Microsoft.AspNetCore.Components.WebAssembly.Hosting

[<EntryPoint>]
let main args =
    let builder = WebAssemblyHostBuilder.CreateDefault(args)
    // Selector must match the <div id="main"> in wwwroot/index.html.
    builder.RootComponents.Add<Main.QrApp>("#main")
    builder.Build().RunAsync() |> ignore
    0
