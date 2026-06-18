// Generates doc/dotnetro/il-support.md: a table of every IL opcode, whether
// DotNetro's IL->MIR translator implements it, and whether at least one lit
// test (src/DotNetro.Compiler.Tests/Lit/*.cs) exercises it.
//
// Rebuild with:
//   dotnet run --project src/DotNetro.Tools.ILSupport
//
// (Build the solution first so the CsCompiler helper it shells out to exists.)
//
// Optional args:
//   --output <path>   override the generated markdown path
//   --root <path>     override the detected repository root

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text;

var repoRoot = GetArg("--root") ?? FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("error: could not locate repository root (no src/DotNetro.slnx found above this binary).");
    return 1;
}

var outputPath = GetArg("--output") ?? Path.Combine(repoRoot, "doc", "dotnetro", "il-support.md");
var translatorPath = Path.Combine(repoRoot, "src", "DotNetro.Compiler", "IlToMirTranslator.cs");
var litDir = Path.Combine(repoRoot, "src", "DotNetro.Compiler.Tests", "Lit");

var implemented = ReadImplementedOpcodes(translatorPath);
Console.Error.WriteLine($"Implemented opcodes parsed from translator: {implemented.Count}");

var covered = ReadLitCoverage(litDir);
Console.Error.WriteLine($"Opcodes exercised by lit tests: {covered.Count}");

// Sanity check: anything a lit test exercises must be implemented (else the
// test could not have compiled). Surface a mismatch loudly rather than hiding it.
foreach (var name in covered)
    if (!implemented.Contains(name))
        Console.Error.WriteLine($"warning: opcode '{name}' is lit-covered but not detected as implemented.");

var markdown = BuildMarkdown(implemented, covered);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, markdown);
Console.Error.WriteLine($"Wrote {outputPath}");
return 0;

// --- argument helpers ---

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Walk up from the running binary until we find the directory holding
// src/DotNetro.slnx.
static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "DotNetro.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}

// Parse the authoritative dispatch switch in the IL->MIR translator: the one
// whose default arm throws "is not supported yet". Every `case ILOpCode.X:`
// label between that switch and its default is a supported opcode. Other
// switches in the file (operand-size skipping, liveness scan) reference
// opcodes the translator only *skips*, so we must not read those.
static HashSet<string> ReadImplementedOpcodes(string translatorPath)
{
    var text = File.ReadAllText(translatorPath);

    var defaultIndex = text.IndexOf("opcode {opCode} is not supported yet", StringComparison.Ordinal);
    if (defaultIndex < 0)
        throw new InvalidOperationException(
            $"Could not find the dispatch switch's default arm in {translatorPath}.");

    var switchIndex = text.LastIndexOf("switch (opCode)", defaultIndex, StringComparison.Ordinal);
    if (switchIndex < 0)
        throw new InvalidOperationException(
            $"Could not find the dispatch switch opening in {translatorPath}.");

    var slice = text[switchIndex..defaultIndex];

    var result = new HashSet<string>(StringComparer.Ordinal);
    foreach (System.Text.RegularExpressions.Match match in
        System.Text.RegularExpressions.Regex.Matches(slice, @"case\s+ILOpCode\.(\w+)\s*:"))
    {
        result.Add(match.Groups[1].Value);
    }

    return result;
}

// Compile each Lit/*.cs via the CsCompiler helper in --opcodes mode and union
// the ILOpCode enum names it reports.
static HashSet<string> ReadLitCoverage(string litDir)
{
    var csCompiler = FindCsCompiler();
    var covered = new HashSet<string>(StringComparer.Ordinal);

    foreach (var file in Directory.EnumerateFiles(litDir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f))
    {
        var psi = new ProcessStartInfo(csCompiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--opcodes");
        psi.ArgumentList.Add(file);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"warning: CsCompiler failed for {file}:\n{stderr}");
            continue;
        }

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            covered.Add(line);
    }

    return covered;
}

// Locate the built CsCompiler binary as a sibling project, matching the path
// pattern the lit-test harness uses.
static string FindCsCompiler()
{
    var config = AppContext.BaseDirectory
        .TrimEnd(Path.DirectorySeparatorChar)
        .Split(Path.DirectorySeparatorChar)[^2];

    var path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        $"../../../../DotNetro.Compiler.Tests.CsCompiler/bin/{config}/net10.0/DotNetro.Compiler.Tests.CsCompiler"));

    var resolved = OperatingSystem.IsWindows() ? path + ".exe" : path;
    if (!File.Exists(resolved))
        throw new FileNotFoundException(
            $"CsCompiler binary not found at {resolved}. Build the solution first (dotnet build src/DotNetro.slnx).");

    return resolved;
}

// Convert an ILOpCode enum name to its IL textual mnemonic: lowercase with
// underscores as dots (e.g. Ldc_i4_0 -> ldc.i4.0, Br_s -> br.s).
static string Mnemonic(string enumName) => enumName.Replace('_', '.').ToLowerInvariant();

static string BuildMarkdown(HashSet<string> implemented, HashSet<string> covered)
{
    var opcodes = Enum.GetValues<ILOpCode>()
        .Select(op => op.ToString())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(Mnemonic, StringComparer.Ordinal)
        .ToList();

    var implementedCount = opcodes.Count(implemented.Contains);
    var coveredCount = opcodes.Count(o => implemented.Contains(o) && covered.Contains(o));

    var sb = new StringBuilder();
    sb.AppendLine("# DotNetro IL opcode support");
    sb.AppendLine();
    sb.AppendLine("<!-- Generated by DotNetro.Tools.ILSupport. Do not edit by hand. -->");
    sb.AppendLine("<!-- Rebuild: dotnet run --project src/DotNetro.Tools.ILSupport -->");
    sb.AppendLine();
    sb.AppendLine("This table lists every ECMA-335 IL opcode (the `System.Reflection.Metadata.ILOpCode`");
    sb.AppendLine("enum), whether DotNetro's IL→MIR translator implements it, and whether at least one");
    sb.AppendLine("lit test under `src/DotNetro.Compiler.Tests/Lit/` exercises it.");
    sb.AppendLine();
    sb.AppendLine($"- **Total opcodes:** {opcodes.Count}");
    sb.AppendLine($"- **Implemented:** {implementedCount}");
    sb.AppendLine($"- **Implemented and lit-covered:** {coveredCount}");
    sb.AppendLine();
    sb.AppendLine("Legend: ✅ yes · ❌ no · — not applicable.");
    sb.AppendLine();
    sb.AppendLine("| Opcode | Implemented | Lit test |");
    sb.AppendLine("|--------|:-----------:|:--------:|");

    foreach (var name in opcodes)
    {
        var isImplemented = implemented.Contains(name);
        var isCovered = covered.Contains(name);

        var implCell = isImplemented ? "✅" : "❌";
        var litCell = !isImplemented ? "—" : isCovered ? "✅" : "❌";

        sb.AppendLine($"| `{Mnemonic(name)}` | {implCell} | {litCell} |");
    }

    return sb.ToString();
}
