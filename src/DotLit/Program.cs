using System.CommandLine;

using DotLit;

var regenerateOption = new Option<string>("--regenerate")
{
    Description = "Folder or file of lit tests whose CHECK directives to regenerate to match current RUN output.",
    Required = true,
};

var rootCommand = new RootCommand("DotLit lit-test tooling.");
rootCommand.Options.Add(regenerateOption);

rootCommand.SetAction(parseResult => Regenerate(parseResult.GetValue(regenerateOption)!));

return rootCommand.Parse(args).Invoke();

static int Regenerate(string path)
{
    var fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
    {
        Console.Error.WriteLine($"Path not found: {fullPath}");
        return 1;
    }

    // Resolve tools against whichever configuration dotlit itself was built/run under (e.g.
    // `dotnet run --project src/DotLit -c Release`), matching how the test harness resolves them.
    var buildConfiguration = LitConfig.CurrentBuildConfiguration();
    LitTestConfiguration ConfigFor(string file) => LitConfig.Load(file, buildConfiguration);

    var results = Directory.Exists(fullPath)
        ? LitTestRegenerator.RegenerateFolder(fullPath, ConfigFor)
        : [LitTestRegenerator.RegenerateFile(fullPath, ConfigFor(fullPath))];

    var changedCount = 0;
    foreach (var result in results)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), result.FilePath);
        if (result.Changed)
        {
            changedCount++;
            Console.WriteLine($"updated  {relative}");
        }

        foreach (var warning in result.Warnings)
            Console.WriteLine($"  warning: {warning}");
    }

    Console.WriteLine($"Regenerated {changedCount} of {results.Count} file(s).");
    return 0;
}
