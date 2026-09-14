/// Runs the same AnyUnit.Style.FSharp discovery/execution QrLinkPdf.Tests
/// itself uses (see that project's own Program.fs) - Runner.Create scans
/// this assembly (the compiled-in Tests.fs/DegradedTests.fs/OcrTests.fs/
/// ImageToPdfTests.fs, via AssemblyInfo.fs's [<assembly: FSharpStyle>])
/// the exact same way, whether run on the desktop CLR or here under wasm.
/// Deliberately never builds a WebAssemblyHostBuilder/Razor component -
/// there's no UI, just running tests and reporting an exit code, so
/// Blazor's actual browser-hosting machinery is never exercised, only
/// its build-time native linking (see the .fsproj's own comment on why
/// Sdk.BlazorWebAssembly is used anyway).
module QrLinkPdf.Tests.Wasm.Program

open System
open System.IO
open System.Reflection
open AnyUnit.Run

/// Where the JSON results land inside the wasm process's own virtual
/// filesystem - NOT a real host path. .NET's File I/O here only ever
/// reaches Mono's Emscripten-backed virtual FS (confirmed the hard way,
/// the same lesson runtests.mjs's own tessdata-loading comment already
/// documents for reads); getting this back out to a real file on disk is
/// runtests.mjs's job, after runMain() resolves.
let resultsPath = "/anyunit-results.json"

[<EntryPoint>]
let main _ =
    let runner = Runner.Create("wasm", [ Assembly.GetExecutingAssembly() ])
    let mutable failures = 0
    runner.RunAll(fun result ->
        match result.Kind with
        | ResultKind.Fail | ResultKind.Error ->
            failures <- failures + 1
            printfn "FAIL %s" result.Test.Name
            if not (System.String.IsNullOrEmpty result.Output) then
                printfn "%s" result.Output
        | _ ->
            printfn "ok   %s" result.Test.Name)
    printfn ""
    printfn "Total: %d, Failures: %d" runner.Tests.Count failures

    // test.yml's own AnyUnit.Report step reads this (once runtests.mjs
    // has bridged it out to a real file) - `Runner` (an IJsonSerialize
    // itself, same shape as ResultsFile/Result) already carries every
    // result RunAll just produced, so this is the same AnyUnit JSON
    // schema anyunit-runner's own `-o`/`-output` writes, no extra
    // accumulation needed on this end.
    File.WriteAllText(resultsPath, runner.ToListJson())

    if failures = 0 then 0 else 1
