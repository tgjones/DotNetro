using Irie.Mir;

namespace Irie.Passes;

// =============================================================================
// RaTrace — gated register-allocation tracing (Stage R0 characterization).
// =============================================================================
//
// A diagnostic facility for understanding the greedy allocator's round-by-round
// behaviour while the splitter/spiller rework (notes/splitter-spiller-rework-plan.md)
// is in flight. It is OFF by default and has ZERO effect on normal compilation
// or test output: every method short-circuits on `Enabled` (read once from the
// `IRIE_RA_TRACE` environment variable), and when disabled nothing is written.
//
// Enable with:  IRIE_RA_TRACE=1   (any non-empty, non-"0"/"false" value)
//
// Output goes to stderr so it never pollutes the `--emit=…` stdout stream that
// the lit harness diffs. The format is line-oriented and grep-friendly:
//
//   [ra] @func round N: assign %v -> $reg
//   [ra] @func round N: SPILL %v (class <name>)
//   [ra] @func round N: split %v (kind <kind>)
//   [ra] @func round N: post-split IR:
//   <full function MIR>
//
// Determinism note: this only observes; it never changes allocation decisions,
// so trace-on and trace-off allocate identically.
internal static class RaTrace
{
    // Read once. Treat unset / empty / "0" / "false" as off; anything else on.
    public static readonly bool Enabled = ComputeEnabled();

    private static bool ComputeEnabled()
    {
        var v = Environment.GetEnvironmentVariable("IRIE_RA_TRACE");
        if (string.IsNullOrEmpty(v)) return false;
        return v is not ("0" or "false" or "False" or "FALSE");
    }

    // A round-scoped assignment of a vreg to a physreg.
    public static void Assign(string func, int round, int vreg, int physReg, string? physRegName)
    {
        if (!Enabled) return;
        var reg = physRegName is null ? $"${physReg}" : $"${physRegName}";
        Console.Error.WriteLine($"[ra] @{func} round {round}: assign %{vreg} -> {reg}");
    }

    // A value that reached the spill rung this round (the pass will rewrite it).
    public static void Spill(string func, int round, int vreg, string className)
    {
        if (!Enabled) return;
        Console.Error.WriteLine($"[ra] @{func} round {round}: SPILL %{vreg} (class {className})");
    }

    // A value that was split this round (which split kind fired).
    public static void Split(string func, int round, int vreg, string kind)
    {
        if (!Enabled) return;
        Console.Error.WriteLine($"[ra] @{func} round {round}: split %{vreg} (kind {kind})");
    }

    // A free-form note (e.g. eviction, deferral) tagged to a round.
    public static void Note(string func, int round, string message)
    {
        if (!Enabled) return;
        Console.Error.WriteLine($"[ra] @{func} round {round}: {message}");
    }

    // One-shot dump of the whole function MIR after a split edit is applied, so
    // the post-split live-range shape can be inspected against the trace. Routed
    // through MirModule.Write (a temporary single-function module) so the printed
    // form matches every other MIR dump (vreg annotations, live-ins, …).
    public static void DumpFunction(string func, int round, MirFunction function, string label)
    {
        if (!Enabled) return;
        Console.Error.WriteLine($"[ra] @{func} round {round}: {label}:");
        var temp = new MirModule();
        temp.Functions.Add(function);
        var sw = new StringWriter();
        temp.Write(sw);
        Console.Error.Write(sw.ToString());
        Console.Error.WriteLine();
    }
}
