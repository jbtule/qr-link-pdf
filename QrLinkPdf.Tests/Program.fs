/// Hand-written MTP entry point, replacing AnyUnit.TestingPlatform's own
/// EnableAnyUnitRunner codegen (its build/*.targets writes this exact
/// same shape as a generated C# file, which an .fsproj can't compile -
/// hence writing it directly instead of using that property). Making
/// this project runnable directly (OutputType=Exe in the .fsproj) means
/// `dotnet test`/`dotnet run` here needs no separate satellite project.
module QrLinkPdf.Tests.Program

open Microsoft.Testing.Platform.Builder
open AnyUnit.TestingPlatform

[<EntryPoint>]
let main args =
    task {
        let! builder = TestApplication.CreateBuilderAsync(args)
        builder.AddAnyUnitTestFramework()
        use! app = builder.BuildAsync()
        return! app.RunAsync()
    }
    |> fun t -> t.GetAwaiter().GetResult()
