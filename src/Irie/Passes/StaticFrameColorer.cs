using Irie.Dialects.Call;
using Irie.Mir;

namespace Irie.Passes;

// Pure, target-agnostic helper that performs the bottom-up "static stack"
// colouring used to lay out non-reentrant functions' frames in a fixed budget
// (e.g. a zero-page window). It is NOT a wired pass — a target's placement pass
// constructs it, supplies the policy (eligibility, per-function footprint,
// budget size), and reads back each promoted function's frame base.
//
// === Static-stack colouring (the model) ===
//
// For a value held in a frame slot to survive a call, a function's frame must be
// DISJOINT from every transitive callee's frame. We lay frames out like a static
// stack, bottom-up:
//
//     base(f)      = max over direct callees c of (base(c) + footprint(c))   (leaf → 0)
//     footprint(f) = perFunctionFootprint(f)  if f is eligible(f) AND
//                                                 base(f)+footprint(f) ≤ budget
//                    else 0
//
// so f's frame sits ABOVE all its callees' frames. Independent subtrees of the
// call graph compute their bases independently and therefore reuse the same
// budget, so total budget use = the largest frame footprint along any single
// call path, not the sum over all functions. A function whose footprint is 0
// (not promoted) reserves no budget, so it doesn't push its callers' frames up.
//
// The maths is entirely generic; the budget value and the eligibility/footprint
// policy come from the caller, so no zero-page or budget literal lives here.
public sealed class StaticFrameColorer
{
    private readonly List<MirFunction> _functions;
    private readonly Func<MirFunction, bool> _eligibility;
    private readonly Func<MirFunction, int> _perFunctionFootprint;
    private readonly int _budgetSize;

    public StaticFrameColorer(
        IReadOnlyList<MirFunction> functions,
        Func<MirFunction, bool> eligibility,
        Func<MirFunction, int> perFunctionFootprint,
        int budgetSize)
    {
        _functions = [.. functions];
        _eligibility = eligibility;
        _perFunctionFootprint = perFunctionFootprint;
        _budgetSize = budgetSize;
    }

    // Runs the colouring and returns, for each promoted function (footprint > 0),
    // a map from the function to its frame base within the budget. Functions not
    // present in the result stay in their default placement (the budget overflows,
    // or they are ineligible / empty).
    public Dictionary<MirFunction, int> Colour()
    {
        var indexByName = new Dictionary<string, int>(_functions.Count);
        for (var i = 0; i < _functions.Count; i++)
            indexByName[_functions[i].Name] = i;

        var callees = BuildCallGraph(_functions, indexByName);

        var eligible = new bool[_functions.Count];
        var size = new int[_functions.Count];
        for (var i = 0; i < _functions.Count; i++)
        {
            eligible[i] = _eligibility(_functions[i]);
            size[i] = _perFunctionFootprint(_functions[i]);
        }

        var footprint = new int[_functions.Count];
        var baseOf = new int[_functions.Count];
        var resolved = new bool[_functions.Count];
        var onStack = new bool[_functions.Count];
        for (var i = 0; i < _functions.Count; i++)
            Resolve(i, callees, size, eligible, footprint, baseOf, resolved, onStack);

        var result = new Dictionary<MirFunction, int>();
        for (var i = 0; i < _functions.Count; i++)
            if (footprint[i] > 0)
                result[_functions[i]] = baseOf[i];
        return result;
    }

    // Bottom-up resolution of base + footprint with a cycle guard. A reentrant or
    // ineligible function (footprint 0) reserves no budget. footprint(f) = size(f)
    // if f is eligible AND base(f)+size(f) fits the budget, else 0.
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
        footprint[i] = eligible[i] && maxBelow + size[i] <= _budgetSize ? size[i] : 0;
        resolved[i] = true;
        onStack[i] = false;
    }

    // Precise direct-call adjacency: an edge i → j iff function i contains a
    // call.func instruction whose Symbol operand names function j. call.indirect
    // is ignored (its caller is already reentrant ⇒ ineligible); non-call Symbol
    // operands (e.g. mem.symbol @data) never appear on a call.func op.
    //
    // We build a precise direct-call graph here rather than reusing
    // ReentrancyAnalysis's graph, which deliberately over-approximates indirect
    // dispatch with a conservative edge from each indirect caller to EVERY
    // function — correct for the reentrancy fixpoint but it would wildly
    // over-reserve the budget here.
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
