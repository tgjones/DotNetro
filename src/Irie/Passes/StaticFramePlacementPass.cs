using Irie.Dialects.Call;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// Target-agnostic, module-level pass that decides — BEFORE frame lowering — where
// each function's FrameSlots live: in a target-supplied zero-page window, or in
// absolute memory (the default). It only records the decision on each slot
// (slot.Placement); FrameLoweringPass then materialises and the instruction
// selector lowers each access against the decided location. There is no
// opcode rewrite and no second pass — see notes/mos6502-frame-placement-plan.md
// §3 (and the impl notes' "Target-agnostic placement pass" section).
//
// === Static-stack colouring (the model) ===
//
// A non-reentrant function (ReentrancyAnalysis / MirFunction.IsNonReentrant) is
// never active on the call stack more than once, so its locals can live in fixed
// static memory rather than on a real stack. For a value held in a frame slot to
// survive a call, a function's frame must be DISJOINT from every transitive
// callee's frame. We lay frames out like a static stack, bottom-up:
//
//     base(f)      = max over direct callees c of (base(c) + footprint(c))   (leaf → 0)
//     size(f)      = Σ slot.Type bytes over f's slots
//     footprint(f) = size(f)  if f is eligible AND base(f)+size(f) ≤ budget
//                    else 0
//
// so f's frame sits ABOVE all its callees' frames. Independent subtrees of the
// call graph compute their bases independently and therefore reuse the same zero
// page, so total budget use = the largest frame footprint along any single call
// path, not the sum over all functions. A function whose footprint is 0 (kept in
// absolute memory) reserves no window, so it doesn't push its callers' frames up.
//
// === Generic algorithm, target-supplied budget ===
//
// The colouring is entirely target-agnostic; only the byte window comes from the
// target (Target.FreeZeroPage), threaded in via the constructor. The base chip
// target advertises FrameZeroPageWindow.None (Size 0), so base(f)+size(f) ≤ 0 is
// never true for a non-empty frame ⇒ every footprint is 0 ⇒ all slots stay
// Absolute and no codegen changes. A subtarget with a real window promotes the
// frames that fit.
public sealed class StaticFramePlacementPass(FrameZeroPageWindow window) : Pass
{
    public override string Name => "StaticFramePlacement";

    private readonly FrameZeroPageWindow _window = window;

    public override void Run(CompilationContext context)
    {
        var module = context.Module;

        // Reentrancy gates eligibility; idempotent and cheap.
        ReentrancyAnalysis.Run(module);

        var functions = module.Functions;
        var indexByName = new Dictionary<string, int>(functions.Count);
        for (var i = 0; i < functions.Count; i++)
            indexByName[functions[i].Name] = i;

        // Direct-call adjacency over the PRE-ISEL form (callees are call.func
        // @name). We build a precise direct-call graph here rather than reusing
        // ReentrancyAnalysis's graph, which deliberately over-approximates
        // indirect dispatch with a conservative edge from each indirect caller to
        // EVERY function — that is correct for the reentrancy fixpoint but would
        // wildly over-reserve the budget here. Functions that make indirect calls
        // are already marked reentrant (hence ineligible), so we can safely ignore
        // call.indirect and keep the colouring edges precise.
        var callees = BuildCallGraph(functions, indexByName);

        // size(f) = total bytes of f's frame slots.
        var size = new int[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            foreach (var slot in functions[i].FrameSlots)
                size[i] += slot.Type.SizeInBits / 8;

        // Eligibility: a function may be promoted iff it has frame slots and is
        // non-reentrant. No escape check and no register-collision check — the
        // zero-page frame window is a separate namespace from the register file,
        // and placement-before-lowering keeps every slot single-location, so
        // neither special case can arise (plan "hack inventory").
        var eligible = new bool[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            eligible[i] =
                functions[i].FrameSlots.Count > 0 &&
                functions[i].IsNonReentrant;

        // Resolve footprint + base together bottom-up (footprint depends on the
        // base fitting the window).
        var footprint = new int[functions.Count];
        var baseOf = new int[functions.Count];
        var resolved = new bool[functions.Count];
        var onStack = new bool[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            Resolve(i, callees, size, eligible, footprint, baseOf, resolved, onStack);

        // Colour each function whose footprint ended up non-zero (eligible AND its
        // frame fits the window at its computed base). Slot i goes at
        // window.Start + base(f) + offsetWithinFrame; others stay Absolute.
        for (var i = 0; i < functions.Count; i++)
        {
            if (footprint[i] == 0) continue; // absolute (ineligible / overflow / no budget)

            var offset = 0;
            foreach (var slot in functions[i].FrameSlots)
            {
                var address = _window.Start + baseOf[i] + offset;
                slot.Placement = new FrameSlotPlacement.ZeroPage(address);
                offset += slot.Type.SizeInBits / 8;
            }
        }
    }

    // Bottom-up resolution of base + footprint with a cycle guard. A reentrant or
    // ineligible function (footprint 0) reserves no window. footprint(f) = size(f)
    // if f is eligible AND base(f)+size(f) fits the window, else 0.
    private void Resolve(
        int i, HashSet<int>[] callees, int[] size, bool[] eligible,
        int[] footprint, int[] baseOf, bool[] resolved, bool[] onStack)
    {
        if (resolved[i]) return;
        if (onStack[i]) return; // cycle guard: treat as base 0 (over-reserves only)
        onStack[i] = true;

        var maxBelow = 0;
        foreach (var c in callees[i])
        {
            if (c == i) continue;
            Resolve(c, callees, size, eligible, footprint, baseOf, resolved, onStack);
            maxBelow = Math.Max(maxBelow, baseOf[c] + footprint[c]);
        }

        baseOf[i] = maxBelow;
        footprint[i] = eligible[i] && maxBelow + size[i] <= _window.Size ? size[i] : 0;
        resolved[i] = true;
        onStack[i] = false;
    }

    // Precise direct-call adjacency: an edge i → j iff function i contains a
    // call.func instruction whose Symbol operand names function j. call.indirect
    // is ignored (its caller is already reentrant ⇒ ineligible); non-call Symbol
    // operands (e.g. mem.symbol @data) never appear on a call.func op.
    private static HashSet<int>[] BuildCallGraph(
        List<MirFunction> functions, Dictionary<string, int> indexByName)
    {
        var callees = new HashSet<int>[functions.Count];
        for (var i = 0; i < functions.Count; i++)
        {
            callees[i] = [];
            foreach (var block in functions[i].Blocks)
                foreach (var instr in block.Instructions)
                {
                    if (instr.Opcode.Dialect != CallDialect.Id
                        || (CallOp)instr.Opcode.Code != CallOp.Func)
                        continue;
                    foreach (var op in instr.Operands)
                        if (op is Symbol s && indexByName.TryGetValue(s.Name, out var c))
                            callees[i].Add(c);
                }
        }
        return callees;
    }
}
