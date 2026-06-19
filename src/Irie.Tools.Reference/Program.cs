using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;

// irie-report — scores Irie's MOS6502 codegen against the llvm-mos reference corpus.
//
// For each case in ext/llvm-mos-reference/ it pairs the committed llvm-mos final
// assembly (.s) with the matching Irie test (same basename) under
// Lit/CodeGen/MOS6502/LlvmMosReference/, runs `iriec --emit=asm` on the test's
// input MIR, counts real instructions on each side, and writes a markdown report
// with per-case deltas and an aggregate ratio.

var corpusOption = new Option<string?>("--corpus") { Description = "Path to ext/llvm-mos-reference (the llvm-mos corpus)." };
var testsOption = new Option<string?>("--tests") { Description = "Path to the LlvmMosReference Irie test directory." };
var iriecOption = new Option<string?>("--iriec") { Description = "Path to the iriec executable." };
var outOption = new Option<string?>("--out") { Description = "Output markdown path (default: <repo>/doc/irie/llvm-mos-comparison.md)." };

var root = new RootCommand("Score Irie's MOS6502 codegen against the llvm-mos reference corpus.");
root.Options.Add(corpusOption);
root.Options.Add(testsOption);
root.Options.Add(iriecOption);
root.Options.Add(outOption);

root.SetAction(parse =>
{
    var (srcDir, repoRoot) = LocateRepo();

    var corpus = parse.GetValue(corpusOption) ?? Path.Combine(repoRoot, "ext", "llvm-mos-reference");
    var tests = parse.GetValue(testsOption) ?? Path.Combine(srcDir, "Irie.Tests", "Lit", "CodeGen", "MOS6502", "LlvmMosReference");
    var iriec = parse.GetValue(iriecOption) ?? LocateIriec(srcDir);
    var outPath = parse.GetValue(outOption) ?? Path.Combine(repoRoot, "doc", "irie", "llvm-mos-comparison.md");

    if (!Directory.Exists(corpus)) { Console.Error.WriteLine($"corpus not found: {corpus}"); return 1; }
    if (!File.Exists(iriec)) { Console.Error.WriteLine($"iriec not found: {iriec} (build it, or pass --iriec)"); return 1; }

    var cases = new List<CaseResult>();
    foreach (var cFile in Directory.EnumerateFiles(corpus, "*.c", SearchOption.AllDirectories).OrderBy(p => p))
    {
        var category = Path.GetFileName(Path.GetDirectoryName(cFile))!;
        var name = Path.GetFileNameWithoutExtension(cFile);
        var sFile = Path.ChangeExtension(cFile, ".s");

        var (llvmCount, llvmAsm) = File.Exists(sFile)
            ? CountLlvmInstructions(File.ReadAllLines(sFile))
            : (0, "(.s missing — run ext/llvm-mos-reference/build.sh)");

        // The Irie test tree mirrors the corpus subfolder layout, so pair within
        // the matching category directory (basics/, control-flow/, …).
        var testFile = Path.Combine(tests, category, name + ".irie");
        int? irieCount = null;
        string irieAsm;
        string status;
        if (!File.Exists(testFile))
        {
            irieAsm = "(no Irie test — blocked, see LlvmMosReference/README.md)";
            status = "blocked";
        }
        else
        {
            var (ok, output, error) = RunIriec(iriec, testFile);
            if (ok)
            {
                var (c, asm) = CountIrieInstructions(output);
                irieCount = c;
                irieAsm = asm;
                status = "converted";
            }
            else
            {
                irieAsm = "iriec failed:\n" + error;
                status = "error";
            }
        }

        cases.Add(new CaseResult(category, name, status, llvmCount, irieCount, llvmAsm, irieAsm));
    }

    var paired = cases.Where(c => c.IrieCount is { } ic && ic > 0 && c.LlvmCount > 0).ToList();
    var totalLlvm = paired.Sum(c => c.LlvmCount);
    var totalIrie = paired.Sum(c => c.IrieCount!.Value);

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, RenderMarkdown(cases, paired.Count, cases.Count, totalLlvm, totalIrie));

    var pct = totalLlvm == 0 ? 0 : (totalIrie - totalLlvm) * 100.0 / totalLlvm;
    Console.WriteLine($"Wrote {outPath}");
    Console.WriteLine($"Coverage: {paired.Count}/{cases.Count} corpus cases paired.");
    Console.WriteLine($"Aggregate: Irie {totalIrie} vs llvm-mos {totalLlvm} instructions ({pct:+0.0;-0.0;0}% delta).");
    return 0;
});

return root.Parse(args).Invoke();

static (string srcDir, string repoRoot) LocateRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotNetro.slnx")))
        dir = dir.Parent;
    if (dir is null)
        throw new InvalidOperationException("could not locate src/ (DotNetro.slnx) above the running assembly.");
    return (dir.FullName, dir.Parent!.FullName);
}

// iriec is built alongside this tool: derive the same config/tfm path segments.
static string LocateIriec(string srcDir)
{
    var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var tfm = Path.GetFileName(baseDir);
    var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
    var exe = OperatingSystem.IsWindows() ? "iriec.exe" : "iriec";
    return Path.Combine(srcDir, "Irie.Tools.Compiler", "bin", config, tfm, exe);
}

static (bool ok, string stdout, string stderr) RunIriec(string iriec, string testFile)
{
    var psi = new ProcessStartInfo(iriec)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("--target");
    psi.ArgumentList.Add("mos6502");
    psi.ArgumentList.Add("--emit=asm");
    psi.ArgumentList.Add(testFile);

    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode == 0, stdout, stderr);
}

// llvm-mos .s: count mnemonics, skipping directives (.foo), labels (foo:),
// comment-only lines (; ...) and blanks. Trailing "; ..." comments are stripped.
static (int count, string display) CountLlvmInstructions(IEnumerable<string> lines)
{
    var sb = new StringBuilder();
    var count = 0;
    foreach (var raw in lines)
    {
        var line = raw.Replace("\t", "    ");
        var code = StripComment(line, ';').TrimEnd();
        var trimmed = code.Trim();
        sb.Append(raw.Replace("\t", "    ")).Append('\n');
        if (trimmed.Length == 0) continue;
        if (trimmed.StartsWith('.')) continue;            // directive
        if (trimmed.EndsWith(':')) continue;              // label
        count++;
    }
    return (count, sb.ToString().TrimEnd('\n'));
}

// Irie --emit=asm: count mnemonics, skipping labels (foo:) and blanks.
static (int count, string display) CountIrieInstructions(string output)
{
    var sb = new StringBuilder();
    var count = 0;
    foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
    {
        sb.Append(raw).Append('\n');
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) continue;
        if (trimmed.EndsWith(':')) continue;              // label
        count++;
    }
    return (count, sb.ToString().TrimEnd('\n'));
}

static string StripComment(string line, char marker)
{
    var idx = line.IndexOf(marker);
    return idx < 0 ? line : line[..idx];
}

static string RenderMarkdown(List<CaseResult> cases, int pairedCount, int totalCount, int totalLlvm, int totalIrie)
{
    var pct = totalLlvm == 0 ? 0 : (totalIrie - totalLlvm) * 100.0 / totalLlvm;
    var ratio = totalLlvm == 0 ? 0 : (double)totalIrie / totalLlvm;

    var sorted = cases
        .OrderByDescending(c => c.Ratio ?? -1)
        .ThenBy(c => c.Category)
        .ThenBy(c => c.Name)
        .ToList();

    var sb = new StringBuilder();
    sb.Append("<!-- Generated by Irie.Tools.Reference (dotnet run --project src/Irie.Tools.Reference). Do not edit by hand. -->\n\n");
    sb.Append("# Irie vs llvm-mos — MOS6502 codegen\n\n");
    sb.Append($"Irie emits **{FormatPct(pct)}** instructions vs llvm-mos ");
    sb.Append($"(**{totalIrie}** vs **{totalLlvm}**, ratio **{ratio.ToString("0.00", CultureInfo.InvariantCulture)}**) ");
    sb.Append($"across **{pairedCount}/{totalCount}** corpus cases.\n\n");
    sb.Append("Lower ratio is better — 🟢 Irie beats llvm-mos, ⚪ parity (1.00), 🔴 worse. Sorted worst-first. ");
    sb.Append("Blocked cases have no Irie test yet (see the [suite README](../../src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/README.md)).\n\n");

    sb.Append("| Category | Case | Status | llvm-mos | Irie | Δ | Ratio | |\n");
    sb.Append("| --- | --- | --- | --: | --: | --: | --: | :-: |\n");
    foreach (var c in sorted)
    {
        var ratioCell = c.Ratio is { } r ? r.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        var markerCell = c.Ratio is { } rr ? RatioMarker(rr) : "";
        var deltaCell = c.IrieCount is { } ic ? FormatDelta(ic - c.LlvmCount) : "—";
        var irieCell = c.IrieCount?.ToString() ?? "—";
        sb.Append($"| {Esc(c.Category)} | {Esc(c.Name)} | {c.Status} | {c.LlvmCount} | {irieCell} | {deltaCell} | {ratioCell} | {markerCell} |\n");
    }

    sb.Append("\n## Per-case assembly\n\n");
    foreach (var c in sorted)
    {
        var irieCell = c.IrieCount?.ToString() ?? "—";
        var ratioCell = c.Ratio is { } r ? $", ratio {r.ToString("0.00", CultureInfo.InvariantCulture)}" : "";
        sb.Append($"<details>\n<summary>{Esc(c.Category)}/{Esc(c.Name)} — llvm-mos {c.LlvmCount}, Irie {irieCell}{ratioCell}</summary>\n\n");
        sb.Append($"llvm-mos ({c.LlvmCount}):\n\n```asm\n{c.LlvmAsm}\n```\n\n");
        sb.Append($"Irie ({irieCell}):\n\n```asm\n{c.IrieAsm}\n```\n\n");
        sb.Append("</details>\n\n");
    }

    return sb.ToString().TrimEnd('\n') + "\n";
}

// 🟢 Irie better than llvm-mos, ⚪ parity, 🔴 worse.
static string RatioMarker(double r) => r < 1.0 ? "🟢" : r > 1.0 ? "🔴" : "⚪";

static string FormatDelta(int d) => d > 0 ? $"+{d}" : d.ToString();
static string FormatPct(double pct) => pct > 0 ? $"+{pct:0.0}%" : $"{pct:0.0}%";

// Escape the few markdown-significant characters that can appear in a table cell.
static string Esc(string s) => s.Replace("\\", "\\\\").Replace("|", "\\|");

record CaseResult(string Category, string Name, string Status, int LlvmCount, int? IrieCount, string LlvmAsm, string IrieAsm)
{
    public double? Ratio => IrieCount is { } ic && LlvmCount > 0 ? (double)ic / LlvmCount : null;
}
