using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// irie-report — tools for comparing Irie's MOS6502 codegen against the llvm-mos
// reference corpus.
//
//   generate-report   Score every corpus case: pair each committed llvm-mos
//                      final assembly (.s) with the matching Irie test, run
//                      `iriec --emit=asm`, count instructions, and write a
//                      markdown report with per-case deltas and an aggregate
//                      ratio.
//
//   generate-diff      For a single corpus case, run `iriec --print-changed`
//                      and emit an uncommitted HTML artifact that lays the
//                      llvm-mos IR pipeline (from the committed .txt dump) and
//                      the Irie MIR pipeline side by side, stage by stage.

var root = new RootCommand("Compare Irie's MOS6502 codegen against the llvm-mos reference corpus.");
root.Subcommands.Add(BuildGenerateReportCommand());
root.Subcommands.Add(BuildGenerateDiffCommand());
return root.Parse(args).Invoke();

// ---------------------------------------------------------------------------
// generate-report
// ---------------------------------------------------------------------------

static Command BuildGenerateReportCommand()
{
    var corpusOption = new Option<string?>("--corpus") { Description = "Path to ext/llvm-mos-reference (the llvm-mos corpus)." };
    var testsOption = new Option<string?>("--tests") { Description = "Path to the LlvmMosReference Irie test directory." };
    var iriecOption = new Option<string?>("--iriec") { Description = "Path to the iriec executable." };
    var outOption = new Option<string?>("--out") { Description = "Output markdown path (default: <repo>/doc/irie/llvm-mos-comparison.md)." };

    var cmd = new Command("generate-report", "Score Irie's MOS6502 codegen against the whole llvm-mos reference corpus.");
    cmd.Options.Add(corpusOption);
    cmd.Options.Add(testsOption);
    cmd.Options.Add(iriecOption);
    cmd.Options.Add(outOption);

    cmd.SetAction(parse =>
    {
        var (srcDir, repoRoot) = LocateRepo();

        var corpus = parse.GetValue(corpusOption) ?? Path.Combine(repoRoot, "ext", "llvm-mos-reference");
        var tests = parse.GetValue(testsOption) ?? DefaultTestsDir(srcDir);
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

    return cmd;
}

// ---------------------------------------------------------------------------
// generate-diff
// ---------------------------------------------------------------------------

static Command BuildGenerateDiffCommand()
{
    var fileArgument = new Argument<string>("file")
    {
        Description = "A corpus case file, e.g. ext/llvm-mos-reference/basics/add-i8.c (any extension works; the .c/.txt siblings are derived).",
    };
    var testsOption = new Option<string?>("--tests") { Description = "Path to the LlvmMosReference Irie test directory." };
    var iriecOption = new Option<string?>("--iriec") { Description = "Path to the iriec executable." };
    var outOption = new Option<string?>("--out") { Description = "Output HTML path (default: <repo>/artifacts/irie-diff/<category>-<name>.html)." };

    var cmd = new Command("generate-diff", "Emit an HTML stage-by-stage diff of one corpus case (llvm-mos IR vs Irie MIR).");
    cmd.Arguments.Add(fileArgument);
    cmd.Options.Add(testsOption);
    cmd.Options.Add(iriecOption);
    cmd.Options.Add(outOption);

    cmd.SetAction(parse =>
    {
        var (srcDir, repoRoot) = LocateRepo();

        var file = parse.GetValue(fileArgument)!;
        var tests = parse.GetValue(testsOption) ?? DefaultTestsDir(srcDir);
        var iriec = parse.GetValue(iriecOption) ?? LocateIriec(srcDir);

        // A corpus case is identified by its directory (category) and basename;
        // the .txt (llvm pipeline dump) and .c (source) are derived siblings.
        var category = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file)))!;
        var name = Path.GetFileNameWithoutExtension(file);
        var txtFile = Path.ChangeExtension(file, ".txt");
        var cFile = Path.ChangeExtension(file, ".c");
        var testFile = Path.Combine(tests, category, name + ".irie");

        if (!File.Exists(txtFile)) { Console.Error.WriteLine($"llvm-mos IR dump not found: {txtFile} (run ext/llvm-mos-reference/build.sh)"); return 1; }
        if (!File.Exists(testFile)) { Console.Error.WriteLine($"no Irie test for this case: {testFile}"); return 1; }
        if (!File.Exists(iriec)) { Console.Error.WriteLine($"iriec not found: {iriec} (build it, or pass --iriec)"); return 1; }

        // --print-changed dumps the post-pass MIR to stderr after every pass;
        // stdout (the asm) is discarded — we only need the per-stage MIR.
        var (ok, _, stderr) = RunIriec(iriec, testFile, "--print-changed");
        if (!ok) { Console.Error.WriteLine("iriec failed:\n" + stderr); return 1; }

        var llvmStages = ParseLlvmStages(File.ReadAllLines(txtFile));
        var irieStages = ParseIrieStages(stderr, inputMir: File.ReadAllText(testFile));
        var rows = AlignStages(llvmStages, irieStages);

        var cSource = File.Exists(cFile) ? File.ReadAllText(cFile) : null;
        var html = RenderDiffHtml($"{category}/{name}", txtFile, testFile, cSource, rows);

        var outPath = parse.GetValue(outOption) ?? Path.Combine(repoRoot, "artifacts", "irie-diff", $"{category}-{name}.html");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, html);

        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"Stages: {rows.Count(r => r.Llvm is not null && r.Irie is not null)} paired, " +
                          $"{rows.Count(r => r.Llvm is not null && r.Irie is null)} llvm-only, " +
                          $"{rows.Count(r => r.Llvm is null && r.Irie is not null)} Irie-only.");
        return 0;
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// Shared infrastructure
// ---------------------------------------------------------------------------

static (string srcDir, string repoRoot) LocateRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotNetro.slnx")))
        dir = dir.Parent;
    if (dir is null)
        throw new InvalidOperationException("could not locate src/ (DotNetro.slnx) above the running assembly.");
    return (dir.FullName, dir.Parent!.FullName);
}

static string DefaultTestsDir(string srcDir) =>
    Path.Combine(srcDir, "Irie.Tests", "Lit", "CodeGen", "MOS6502", "LlvmMosReference");

// iriec is built alongside this tool: derive the same config/tfm path segments.
static string LocateIriec(string srcDir)
{
    var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var tfm = Path.GetFileName(baseDir);
    var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
    var exe = OperatingSystem.IsWindows() ? "iriec.exe" : "iriec";
    return Path.Combine(srcDir, "Irie.Tools.Compiler", "bin", config, tfm, exe);
}

// Runs `iriec --target mos6502 --emit=asm [extraArgs] <testFile>`, capturing both
// streams. generate-report wants stdout (the asm); generate-diff adds
// --print-changed and wants stderr (the per-stage MIR).
static (bool ok, string stdout, string stderr) RunIriec(string iriec, string testFile, params string[] extraArgs)
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
    foreach (var arg in extraArgs)
        psi.ArgumentList.Add(arg);
    psi.ArgumentList.Add(testFile);

    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode == 0, stdout, stderr);
}

// ---------------------------------------------------------------------------
// generate-report: instruction counting + markdown
// ---------------------------------------------------------------------------

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
    sb.Append("<!-- Generated by Irie.Tools.Reference (dotnet run --project src/Irie.Tools.Reference -- generate-report). Do not edit by hand. -->\n\n");
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

// ---------------------------------------------------------------------------
// generate-diff: pipeline-stage parsing + alignment + HTML
// ---------------------------------------------------------------------------

// Anchored correspondences between the Irie pass name and the llvm-mos pass id
// (the parenthesised pass id in the .txt header). Stages with no anchor render
// single-sided. Several pipeline stages have no counterpart and that is fine —
// these are the few that map cleanly across both GlobalISel-style pipelines.
static (string Irie, string Llvm)[] StageAnchors() =>
[
    ("at-start", "at-start"),                              // initial IR
    ("AbiLowering", "irtranslator"),                       // arg/return lowering into MIR
    ("Legalizer", "legalizer"),                            // legalization
    ("InstructionSelector", "instruction-select"),         // instruction selection
    ("PhiElimination", "phi-node-elimination"),            // PHI / block-arg elimination
    ("TwoAddressInstruction", "twoaddressinstruction"),    // two-address rewriting
    ("RegisterAllocator", "virtregrewriter"),              // register allocation
    ("CopyElimination", "mos-copy-opt"),                   // post-RA copy cleanup
    ("PseudoExpansion", "postrapseudos"),                  // post-RA pseudo expansion
];

// Parse the committed llvm-mos `-print-changed` IR dump into stages. Headers look
// like `*** IR Dump After IRTranslator (irtranslator) on add_i8 ***` or
// `*** IR Dump At Start ***`; "omitted because no change" headers have no body.
static List<Stage> ParseLlvmStages(string[] lines)
{
    var headerRe = new Regex(@"^\*\*\* IR Dump (?<rest>.+?) \*\*\*\s*$");
    var idRe = new Regex(@"\(([^)]+)\)");

    var stages = new List<Stage>();
    string? curId = null, curDisplay = null;
    var curChanged = false;
    var body = new StringBuilder();

    void Flush()
    {
        if (curId is not null)
            stages.Add(new Stage(curId, curDisplay!, curChanged, body.ToString().Trim('\n')));
        body.Clear();
    }

    foreach (var line in lines)
    {
        var m = headerRe.Match(line);
        if (!m.Success) { if (curId is not null) body.Append(line).Append('\n'); continue; }

        Flush();
        var rest = m.Groups["rest"].Value;
        if (rest == "At Start")
        {
            curId = "at-start"; curDisplay = "At Start"; curChanged = true;
            continue;
        }

        rest = rest.StartsWith("After ") ? rest["After ".Length..] : rest;
        curChanged = !rest.Contains("omitted because no change");
        var idMatch = idRe.Match(rest);
        curId = idMatch.Success ? idMatch.Groups[1].Value : SlugDisplay(rest);

        // Display name = text before the "(passid)" / " on fn" / " omitted" tail.
        var cut = rest.Length;
        foreach (var sep in new[] { " (", " on ", " omitted" })
        {
            var i = rest.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0) cut = Math.Min(cut, i);
        }
        curDisplay = rest[..cut].Trim();
    }
    Flush();

    return CarryForwardBodies(stages);
}

// Parse iriec's --print-changed stderr into stages. Headers look like
// `; *** MIR Dump After Legalizer ***` or `… omitted because no change ***`.
// The input MIR is prepended as the "At Start" stage (iriec dumps only *after*
// each pass).
static List<Stage> ParseIrieStages(string stderr, string inputMir)
{
    var headerRe = new Regex(@"^; \*\*\* MIR Dump After (?<rest>.+?) \*\*\*\s*$");

    var stages = new List<Stage> { new("at-start", "Input MIR", true, inputMir.Replace("\r\n", "\n").Trim('\n')) };
    string? curId = null, curDisplay = null;
    var curChanged = false;
    var body = new StringBuilder();

    void Flush()
    {
        if (curId is not null)
            stages.Add(new Stage(curId, curDisplay!, curChanged, body.ToString().Trim('\n')));
        body.Clear();
    }

    foreach (var line in stderr.Replace("\r\n", "\n").Split('\n'))
    {
        var m = headerRe.Match(line);
        if (!m.Success) { if (curId is not null) body.Append(line).Append('\n'); continue; }

        Flush();
        var rest = m.Groups["rest"].Value;
        curChanged = !rest.Contains("omitted because no change");
        var omittedAt = rest.IndexOf(" omitted", StringComparison.Ordinal);
        curDisplay = (omittedAt >= 0 ? rest[..omittedAt] : rest).Trim();
        curId = curDisplay;          // Irie pass name is its own id
    }
    Flush();

    return CarryForwardBodies(stages);
}

// Replace each unchanged stage's (empty) body with the most recent changed body,
// so every stage can be rendered with the IR as it stood at that point.
static List<Stage> CarryForwardBodies(List<Stage> stages)
{
    var last = "";
    var result = new List<Stage>(stages.Count);
    foreach (var s in stages)
    {
        if (s.Changed && s.Body.Length > 0) last = s.Body;
        result.Add(s.Changed ? s : s with { Body = last });
    }
    return result;
}

static string SlugDisplay(string rest)
{
    var i = rest.IndexOf(" on ", StringComparison.Ordinal);
    return (i >= 0 ? rest[..i] : rest).Trim();
}

// Merge-align the two pipelines, keeping each side in pipeline order and pairing
// at the anchored stages. Non-anchored stages stay single-sided; unchanged
// non-anchored stages are dropped to keep the artifact focused (the long tail of
// llvm analysis/no-op passes), while anchored stages always appear even when a
// pass left the IR untouched, so the shared backbone stays visible.
static List<DiffRow> AlignStages(List<Stage> llvm, List<Stage> irie)
{
    var anchors = StageAnchors();
    var llvmAnchorOf = anchors.ToDictionary(a => a.Irie, a => a.Llvm);
    var anchoredLlvmIds = anchors.Select(a => a.Llvm).ToHashSet();

    var rows = new List<DiffRow>();
    var llvmIdx = 0;

    void FlushLlvmUntil(string? targetId)
    {
        while (llvmIdx < llvm.Count && llvm[llvmIdx].Id != targetId)
        {
            var s = llvm[llvmIdx++];
            // Emit non-anchor llvm stages only when they changed something.
            if (s.Changed && !anchoredLlvmIds.Contains(s.Id))
                rows.Add(new DiffRow(s, null));
        }
    }

    foreach (var irieStage in irie)
    {
        if (llvmAnchorOf.TryGetValue(irieStage.Id, out var llvmId))
        {
            FlushLlvmUntil(llvmId);
            Stage? matched = llvmIdx < llvm.Count && llvm[llvmIdx].Id == llvmId ? llvm[llvmIdx++] : null;
            rows.Add(new DiffRow(matched, irieStage));
        }
        else if (irieStage.Changed)
        {
            rows.Add(new DiffRow(null, irieStage));
        }
    }

    FlushLlvmUntil(targetId: null);   // trailing llvm-only changed stages
    return rows;
}

static string RenderDiffHtml(string caseLabel, string txtFile, string testFile, string? cSource, List<DiffRow> rows)
{
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
    sb.Append($"<title>Irie vs llvm-mos — {EscHtml(caseLabel)}</title>\n");
    sb.Append("<style>\n").Append(DiffCss()).Append("</style>\n</head>\n<body>\n");

    sb.Append($"<h1>Irie MIR vs llvm-mos IR — <code>{EscHtml(caseLabel)}</code></h1>\n");
    sb.Append("<p class=\"meta\">Pipeline stages aligned at the shared GlobalISel-style backbone. ");
    sb.Append("Several stages have no counterpart on one side — those render single-sided. ");
    sb.Append("Unchanged anchored passes are shown dimmed; lines that differ from the ");
    sb.Append("previous stage on the same side are <span class=\"chg\">highlighted</span>.</p>\n");
    sb.Append($"<p class=\"meta\">llvm dump: <code>{EscHtml(txtFile)}</code><br>Irie test: <code>{EscHtml(testFile)}</code></p>\n");

    if (cSource is not null)
    {
        sb.Append("<details class=\"source\">\n<summary>C source</summary>\n");
        sb.Append($"<pre>{EscHtml(cSource.Trim('\n'))}</pre>\n</details>\n");
    }

    // The previous body shown on each side, so a stage's body can be line-diffed
    // against the state it inherited (which is the prior displayed stage — any
    // dropped stages in between were no-ops, so they left the body untouched).
    string? prevLlvm = null, prevIrie = null;

    foreach (var row in rows)
    {
        var paired = row.Llvm is not null && row.Irie is not null;
        var llvmOnly = row.Llvm is not null && row.Irie is null;
        var kind = paired ? "paired" : llvmOnly ? "llvm-only" : "irie-only";

        // Collapse the long tail of llvm-only analysis/opt passes; keep the
        // shared and Irie-specific stages expanded.
        var open = llvmOnly ? "" : " open";
        var heading = paired
            ? $"{EscHtml(row.Llvm!.Display)} ↔ {EscHtml(row.Irie!.Display)}"
            : llvmOnly
                ? $"{EscHtml(row.Llvm!.Display)} <span class=\"tag\">llvm-mos only</span>"
                : $"{EscHtml(row.Irie!.Display)} <span class=\"tag\">Irie only</span>";

        sb.Append($"<details class=\"stage {kind}\"{open}>\n<summary>{heading}</summary>\n");
        sb.Append("<div class=\"cols\">\n");
        sb.Append(RenderColumn("llvm-mos", row.Llvm, prevLlvm));
        sb.Append(RenderColumn("Irie", row.Irie, prevIrie));
        sb.Append("</div>\n</details>\n");

        if (row.Llvm is not null) prevLlvm = row.Llvm.Body;
        if (row.Irie is not null) prevIrie = row.Irie.Body;
    }

    sb.Append("</body>\n</html>\n");
    return sb.ToString();
}

static string RenderColumn(string side, Stage? stage, string? prevBody)
{
    if (stage is null)
        return $"<div class=\"col empty\"><div class=\"col-head\">{EscHtml(side)}</div><pre class=\"none\">— no equivalent stage —</pre></div>\n";

    var badge = stage.Changed ? "" : " <span class=\"nochange\">no change</span>";
    var dim = stage.Changed ? "" : " dim";
    return $"<div class=\"col{dim}\"><div class=\"col-head\">{EscHtml(side)} · {EscHtml(stage.Display)}{badge}</div><pre>{RenderBody(stage.Body, prevBody)}</pre></div>\n";
}

// Render a stage body as one block element per line, highlighting lines that
// were added or modified relative to `prevBody` (the body this stage inherited).
// The first stage on a side has no predecessor, so nothing is highlighted.
static string RenderBody(string body, string? prevBody)
{
    var cur = body.Split('\n');
    var changed = prevBody is null ? new bool[cur.Length] : ChangedLines(prevBody.Split('\n'), cur);

    var sb = new StringBuilder();
    for (var i = 0; i < cur.Length; i++)
    {
        // ​ keeps a blank line's box at full line height under display:block.
        var content = cur[i].Length == 0 ? "​" : EscHtml(cur[i]);
        sb.Append(changed[i] ? "<span class=\"ln chg\">" : "<span class=\"ln\">").Append(content).Append("</span>");
    }
    return sb.ToString();
}

// Standard LCS line diff: returns, for each line of `cur`, whether it is *not*
// part of the longest common subsequence with `prev` — i.e. an added/changed
// line worth highlighting. Unchanged (matched) lines map to false.
static bool[] ChangedLines(string[] prev, string[] cur)
{
    int n = prev.Length, m = cur.Length;
    var dp = new int[n + 1, m + 1];
    for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            dp[i, j] = prev[i] == cur[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

    var changed = new bool[m];
    int a = 0, b = 0;
    while (a < n && b < m)
    {
        if (prev[a] == cur[b]) { a++; b++; }              // matched — unchanged
        else if (dp[a + 1, b] >= dp[a, b + 1]) a++;       // line only in prev (removed)
        else changed[b++] = true;                         // line only in cur (added)
    }
    while (b < m) changed[b++] = true;                    // trailing additions
    return changed;
}

static string EscHtml(string s) => s
    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

static string DiffCss() => """
:root { color-scheme: light dark; }
body { font: 14px/1.45 -apple-system, Segoe UI, sans-serif; margin: 2rem; max-width: 1400px; }
h1 { font-size: 1.3rem; }
h1 code { font-size: inherit; }
.meta { color: #666; font-size: 0.85rem; margin: 0.3rem 0; }
code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
details.source { margin: 1rem 0; }
details.stage { border: 1px solid #ccc; border-radius: 6px; margin: 0.6rem 0; }
details.stage > summary { cursor: pointer; padding: 0.5rem 0.8rem; font-weight: 600; }
details.stage.paired > summary { background: #eef4ff; }
details.stage.llvm-only > summary { background: #fff4ec; }
details.stage.irie-only > summary { background: #ecfff0; }
@media (prefers-color-scheme: dark) {
  details.stage.paired > summary { background: #1d2740; }
  details.stage.llvm-only > summary { background: #3a2a1a; }
  details.stage.irie-only > summary { background: #16301d; }
}
.tag { font-weight: 400; font-size: 0.8rem; opacity: 0.7; }
.cols { display: grid; grid-template-columns: 1fr 1fr; gap: 0.6rem; padding: 0.6rem; }
.col { border: 1px solid #ddd; border-radius: 4px; overflow: hidden; min-width: 0; }
.col.dim { opacity: 0.55; }
.col-head { font-size: 0.8rem; font-weight: 600; padding: 0.3rem 0.5rem; background: #f3f3f3; }
@media (prefers-color-scheme: dark) { .col-head { background: #2a2a2a; } }
.nochange { font-weight: 400; opacity: 0.6; font-style: italic; }
pre { margin: 0; padding: 0.5rem 0; overflow-x: auto; white-space: pre;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; }
pre .ln { display: block; padding: 0 0.7rem; }
.chg { background: #fff3b0; box-shadow: inset 3px 0 #e0a800; }
@media (prefers-color-scheme: dark) { .chg { background: #4d3f00; box-shadow: inset 3px 0 #b58900; } }
pre.none { opacity: 0.5; font-style: italic; padding: 0.5rem 0.7rem; }
""";

record CaseResult(string Category, string Name, string Status, int LlvmCount, int? IrieCount, string LlvmAsm, string IrieAsm)
{
    public double? Ratio => IrieCount is { } ic && LlvmCount > 0 ? (double)ic / LlvmCount : null;
}

// One dump in a pipeline. `Changed` is false for a pass that ran but left the IR
// untouched (llvm: "omitted because no change"); its `Body` is carried forward
// from the last pass that did change it, so every stage has a renderable body.
record Stage(string Id, string Display, bool Changed, string Body);

// A row in the diff: either side may be null (no counterpart stage).
record DiffRow(Stage? Llvm, Stage? Irie);
