/// Hand-written MTP entry point, replacing AnyUnit.TestingPlatform's own
/// EnableAnyUnitRunner codegen (its build/*.targets writes this exact
/// same shape as a generated C# file, which an .fsproj can't compile -
/// hence writing it directly instead of using that property). Making
/// this project runnable directly (OutputType=Exe in the .fsproj) means
/// `dotnet test`/`dotnet run` here needs no separate satellite project.
///
/// Tried switching to EnableAnyUnitRunner directly when bumping to
/// AnyUnit 1.1.0 (its own .targets comment now claims the official
/// Microsoft.Testing.Platform.MSBuild generator emits real F# source,
/// not just C#) - and that part is true, but the generated entry point
/// still never actually gets compiled in for this project: reproduced
/// with Microsoft.Testing.Platform.MSBuild pinned all the way up to
/// 2.3.3 (which does fix an *earlier*, different F#-exclusion bug in
/// that package's own .targets - the `<Compile Include>` for the
/// generated file used to unconditionally skip F# via `Condition
/// ="'$(Language)' != 'F#'"` with no F# counterpart at all), confirmed
/// with a from-scratch minimal repro outside this repo too: no
/// generated source file appears under obj/ at all, and the built
/// assembly has no working entry point ("Main module of program is
/// empty" / zero tests discovered). Worth chasing upstream in AnyUnit's
/// own repo; not something fixable from here. Keeping this hand-written
/// entry point until that's resolved.
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
