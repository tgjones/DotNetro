using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

// irie-report — scores Irie's MOS6502 codegen against the llvm-mos reference corpus.
//
// For each case in ext/llvm-mos-reference/ it pairs the committed llvm-mos final
// assembly (.s) with the matching Irie test (same basename) under
// Lit/CodeGen/MOS6502/LlvmMosReference/, runs `iriec --emit=asm` on the test's
// input MIR, counts real instructions on each side, and writes an HTML report
// with per-case deltas and an aggregate ratio.

var corpusOption = new Option<string?>("--corpus") { Description = "Path to ext/llvm-mos-reference (the llvm-mos corpus)." };
var testsOption = new Option<string?>("--tests") { Description = "Path to the LlvmMosReference Irie test directory." };
var iriecOption = new Option<string?>("--iriec") { Description = "Path to the iriec executable." };
var outOption = new Option<string?>("--out") { Description = "Output HTML path (default: <corpus>/report.html)." };

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
    var outPath = parse.GetValue(outOption) ?? Path.Combine(corpus, "report.html");

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

    File.WriteAllText(outPath, RenderHtml(cases, paired.Count, cases.Count, totalLlvm, totalIrie));

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

static string RenderHtml(List<CaseResult> cases, int pairedCount, int totalCount, int totalLlvm, int totalIrie)
{
    var pct = totalLlvm == 0 ? 0 : (totalIrie - totalLlvm) * 100.0 / totalLlvm;
    var ratio = totalLlvm == 0 ? 0 : (double)totalIrie / totalLlvm;

    var rows = new StringBuilder();
    var n = 0;
    foreach (var c in cases.OrderByDescending(c => c.Ratio ?? -1).ThenBy(c => c.Category).ThenBy(c => c.Name))
    {
        var id = $"case{n++}";
        var ratioCell = c.Ratio is { } r ? r.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        var deltaCell = c.IrieCount is { } ic ? FormatDelta(ic - c.LlvmCount) : "—";
        var irieCell = c.IrieCount?.ToString() ?? "—";
        var color = c.Ratio is { } rr ? RatioColor(rr) : "#3a3a3a";
        rows.Append($"""
            <tr class="row" data-ratio="{(c.Ratio ?? -1).ToString(CultureInfo.InvariantCulture)}" onclick="toggle('{id}')">
              <td>{Esc(c.Category)}</td>
              <td class="name">{Esc(c.Name)}</td>
              <td><span class="status {c.Status}">{c.Status}</span></td>
              <td class="num">{c.LlvmCount}</td>
              <td class="num">{irieCell}</td>
              <td class="num">{deltaCell}</td>
              <td class="num"><span class="ratio" style="background:{color}">{ratioCell}</span></td>
            </tr>
            <tr id="{id}" class="detail" style="display:none"><td colspan="7"><div class="cols">
              <div><h4>llvm-mos ({c.LlvmCount})</h4><pre>{Esc(c.LlvmAsm)}</pre></div>
              <div><h4>Irie ({irieCell})</h4><pre>{Esc(c.IrieAsm)}</pre></div>
            </div></td></tr>

            """);
    }

    return $$"""
        <!doctype html><html><head><meta charset="utf-8">
        <title>Irie vs llvm-mos codegen</title>
        <style>
          body { font: 14px/1.5 -apple-system, system-ui, sans-serif; margin: 2rem; background:#1e1e1e; color:#ddd; }
          h1 { font-size: 1.4rem; }
          .headline { font-size: 1.6rem; margin: .5rem 0 1.5rem; }
          .headline b { color:#fff; }
          .meta { color:#999; margin-bottom:1rem; }
          table { border-collapse: collapse; width: 100%; }
          th, td { padding: .4rem .6rem; text-align: left; border-bottom: 1px solid #333; }
          th { cursor: pointer; color:#bbb; user-select:none; }
          .row { cursor: pointer; }
          .row:hover { background:#2a2a2a; }
          .num { text-align: right; font-variant-numeric: tabular-nums; }
          .name { font-weight:600; color:#fff; }
          .ratio { padding:.1rem .45rem; border-radius:4px; color:#111; font-weight:600; }
          .status { padding:.1rem .4rem; border-radius:4px; font-size:.8rem; }
          .status.converted { background:#2d4a2d; color:#9d9; }
          .status.blocked { background:#4a3a2d; color:#db9; }
          .status.error { background:#4a2d2d; color:#d99; }
          .detail pre { background:#111; padding:.8rem; border-radius:6px; overflow:auto; font: 12px/1.4 ui-monospace, monospace; }
          .cols { display:grid; grid-template-columns:1fr 1fr; gap:1rem; }
          h4 { margin:.3rem 0; color:#bbb; }
        </style></head><body>
        <h1>Irie vs llvm-mos — MOS6502 codegen</h1>
        <div class="headline">Irie emits <b>{{FormatPct(pct)}}</b> instructions vs llvm-mos
          (<b>{{totalIrie}}</b> vs <b>{{totalLlvm}}</b>, ratio <b>{{ratio.ToString("0.00", CultureInfo.InvariantCulture)}}</b>)
          across <b>{{pairedCount}}/{{totalCount}}</b> corpus cases.</div>
        <div class="meta">Lower ratio is better (1.00 = parity). Click a row for side-by-side assembly.
          Sorted worst-first. Blocked cases have no Irie test yet (see LlvmMosReference/README.md).</div>
        <table id="t"><thead><tr>
          <th onclick="sortBy(0,false)">Category</th>
          <th onclick="sortBy(1,false)">Case</th>
          <th onclick="sortBy(2,false)">Status</th>
          <th onclick="sortBy(3,true)">llvm-mos</th>
          <th onclick="sortBy(4,true)">Irie</th>
          <th onclick="sortBy(5,true)">Δ</th>
          <th onclick="sortBy(6,true)">ratio</th>
        </tr></thead><tbody>
        {{rows}}
        </tbody></table>
        <script>
          function toggle(id){ var e=document.getElementById(id); e.style.display = e.style.display==='none'?'table-row':'none'; }
          function sortBy(col, numeric){
            var tb=document.querySelector('#t tbody');
            var rows=[...tb.querySelectorAll('tr.row')].map(r=>[r, r.nextElementSibling]);
            rows.sort((a,b)=>{
              var x=a[0].children[col].innerText.trim(), y=b[0].children[col].innerText.trim();
              if(numeric){ x=parseFloat(x.replace('+',''))||-1e9; y=parseFloat(y.replace('+',''))||-1e9; return y-x; }
              return x.localeCompare(y);
            });
            tb.innerHTML=''; rows.forEach(p=>{tb.appendChild(p[0]); tb.appendChild(p[1]);});
          }
        </script>
        </body></html>
        """;
}

static string FormatDelta(int d) => d > 0 ? $"+{d}" : d.ToString();
static string FormatPct(double pct) => pct > 0 ? $"+{pct:0.0}%" : $"{pct:0.0}%";

// Green at parity (1.0), through yellow, to red as the ratio worsens (>= 2.5).
static string RatioColor(double r)
{
    var t = Math.Clamp((r - 1.0) / 1.5, 0, 1);
    int red = (int)(0x6c + t * (0xd9 - 0x6c));
    int grn = (int)(0xd9 - t * (0xd9 - 0x6c));
    return $"#{red:x2}{grn:x2}6c";
}

static string Esc(string s) => WebUtility.HtmlEncode(s);

record CaseResult(string Category, string Name, string Status, int LlvmCount, int? IrieCount, string LlvmAsm, string IrieAsm)
{
    public double? Ratio => IrieCount is { } ic && LlvmCount > 0 ? (double)ic / LlvmCount : null;
}
