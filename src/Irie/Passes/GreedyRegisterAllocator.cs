using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// GreedyRegisterAllocator — an LLVM/llvm-mos-style greedy allocator (skeleton).
// =============================================================================
//
// Reference: llvm/lib/CodeGen/RegAllocGreedy.cpp (`selectOrSplitImpl`). This is
// the alternative engine to GraphColouringAllocator, being brought up under the
// `RegisterAllocatorPass(useGreedy: true)` flag (greedy-RA plan, Stage 0). It is
// NOT yet the default.
//
// ----------------------------------------------------------------------------
// Directionally-complete skeleton (minimal rungs)
// ----------------------------------------------------------------------------
// The whole greedy `selectOrSplit` LADDER is present from the start so the
// architecture matches the reference; the individual rungs are minimal and get
// fleshed out in later stages:
//
//   tryAssign  — REAL. Find a free physreg from the value's class-intersection
//                allowed set (RegisterAllocationSupport.ComputeAllowedColours),
//                preferring a copy-hint colour so copies collapse.
//   tryEvict   — STUB (returns none). Stage 1 implements weight-based eviction
//                with cascade loop-prevention.
//   trySplit   — STUB (returns false). Stage 2 builds the SplitEditor analogue
//                + tryLocalSplit; Stage 3 adds split-around-call. For now an
//                unassignable value falls straight through to spill, which the
//                surrounding RegisterAllocatorPass.SpillVregs already handles
//                (rematerialize / split-to-register / store-reload).
//   spill      — delegated. We record the actual spill and return null; the
//                pass rewrites it and re-runs us with fresh intervals.
//
// ----------------------------------------------------------------------------
// Reanalysis instead of incremental liveness
// ----------------------------------------------------------------------------
// RegisterAllocatorPass already recomputes LiveIntervals each round of its spill
// loop. So whenever this engine edits the IR (a split or a spill) it returns
// null immediately; the pass reanalyses and re-runs. That removes any need for
// LLVM's incremental transferValues machinery — a fresh LiveIntervalsAnalysis
// reconstructs all liveness. The cost is one IR edit per round; acceptable for
// the function sizes this target sees.
//
// ----------------------------------------------------------------------------
// LiveRangeStage
// ----------------------------------------------------------------------------
// Each vreg carries a stage (New → Split → Spill), persisted ACROSS rounds by
// the pass (so a value deferred for the "second round" picture, or a split
// product, keeps its stage). The ladder bounds work: a value is given one
// deferral, then one split attempt, then it spills — so allocation terminates.
//
// Determinism: the priority queue dequeues largest-interval-first, ties broken
// by ascending vreg id — the same discipline the colourer follows, so the
// characterization lit tests stay stable.
internal sealed class GreedyRegisterAllocator
{
    private readonly MirFunction _function;
    private readonly TargetRegisterInfo _registerInfo;
    private readonly LiveIntervals _intervals;
    private readonly IReadOnlySet<int> _unspillable;

    // Per-vreg allocation stage, OWNED by the pass and shared across rounds.
    private readonly Dictionary<int, LiveRangeStage> _stages;

    // The committed colouring built up over one Run.
    private readonly Dictionary<int, int> _assignment = [];
    // Reverse index: physreg → vregs already assigned to it (the LiveRegMatrix
    // analogue, kept as a vreg list so interference is an interval overlap test
    // against each — precoloured physreg busy windows are queried directly off
    // _intervals.PhysRegIntervals via LiveIntervals.Overlaps).
    private readonly Dictionary<int, List<int>> _assignedTo = [];

    // Class-intersection allowed registers per vreg (preference-ordered).
    private Dictionary<int, int[]> _allowed = [];

    // Actual spills this round (a value that reached the spill rung). Read by the
    // pass, which rewrites them to memory traffic before re-running.
    private readonly List<int> _actualSpills = [];
    public IReadOnlyList<int> SpilledVregs => _actualSpills;

    public GreedyRegisterAllocator(
        MirFunction function, TargetRegisterInfo registerInfo, LiveIntervals intervals,
        Dictionary<int, LiveRangeStage> stages, IReadOnlySet<int>? unspillable = null)
    {
        _function = function;
        _registerInfo = registerInfo;
        _intervals = intervals;
        _stages = stages;
        _unspillable = unspillable ?? new HashSet<int>();
    }

    // Returns the final vreg→physreg colouring, or null if the engine edited the
    // IR (a spill — or, in later stages, a split). On null, SpilledVregs lists
    // any spilled vregs the pass must rewrite before re-running.
    public Dictionary<int, int>? Run()
    {
        var vregs = RegisterAllocationSupport.CollectReferencedVregs(_function);
        _allowed = RegisterAllocationSupport.ComputeAllowedColours(_function, _registerInfo, vregs);

        // Priority queue: largest interval first, ties by ascending vreg id.
        var queue = new PriorityQueue<int, (long, int)>();
        foreach (var v in vregs)
            queue.Enqueue(v, Priority(v));

        while (queue.Count > 0)
        {
            var vreg = queue.Dequeue();

            // 1. Try a free register.
            if (TryAssign(vreg) is int phys)
            {
                Commit(vreg, phys);
                continue;
            }

            var stage = GetStage(vreg);

            // 2. Try to evict lighter interferences — but RS_Split ranges already
            //    failed and don't get a second chance until they have split.
            if (stage != LiveRangeStage.Split && TryEvict(vreg) is int evicted)
            {
                Commit(vreg, evicted);
                continue;
            }

            // 3. First time we couldn't place it: defer to the second round so
            //    all smaller ranges allocate first and we see the real
            //    interference to split around (selectOrSplitImpl "wait for
            //    second round").
            if (stage < LiveRangeStage.Split)
            {
                SetStage(vreg, LiveRangeStage.Split);
                queue.Enqueue(vreg, Priority(vreg));
                continue;
            }

            // 4. Try splitting the range (stub for now → falls through to spill).
            if (stage < LiveRangeStage.Spill && TrySplit(vreg))
                return null; // IR edited → pass reanalyses and re-runs.

            // 5. Spill. The pass rewrites it (remat / split-to-register /
            //    store-reload) and re-runs us with fresh intervals.
            _actualSpills.Add(vreg);
            return null;
        }

        return _assignment;
    }

    // -------------------------------------------------------------------------
    // tryAssign — pick a free physreg from the value's allowed set, copy-hint
    // first so the copy collapses to an identity.
    // -------------------------------------------------------------------------
    private int? TryAssign(int vreg)
    {
        foreach (var candidate in PreferenceOrder(vreg))
            if (IsInterferenceFree(vreg, candidate))
                return candidate;
        return null;
    }

    // Candidate colours in preference order: copy-hint colours first (the other
    // end of a pseudo.copy that is already a physreg or already assigned), then
    // the class-intersection allowed order. Quality refinements (short-range GPR
    // preference, neighbour-hint avoidance) are deferred — Stage 1+.
    private IEnumerable<int> PreferenceOrder(int vreg)
    {
        var allowed = _allowed[vreg];
        var emitted = new HashSet<int>();

        foreach (var hint in CopyHintColours(vreg))
            if (RegisterAllocationSupport.Contains(allowed, hint) && emitted.Add(hint))
                yield return hint;

        foreach (var c in allowed)
            if (emitted.Add(c))
                yield return c;
    }

    // The already-known colours at the other end of every pseudo.copy this vreg
    // takes part in: a physreg operand directly, or an already-assigned vreg.
    private IEnumerable<int> CopyHintColours(int vreg)
    {
        foreach (var block in _function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
                if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
                if (instr.Operands.Length != 2) continue;

                var (a, b) = (instr.Operands[0], instr.Operands[1]);
                var other = OperandIsVreg(a, vreg) ? b : OperandIsVreg(b, vreg) ? a : null;
                if (other is null) continue;

                switch (other)
                {
                    case PhysicalReg p:
                        yield return p.Id;
                        break;
                    case VirtualReg ov when _assignment.TryGetValue(ov.Id, out var c):
                        yield return c;
                        break;
                }
            }
    }

    private static bool OperandIsVreg(MirOperand op, int vreg) =>
        op is VirtualReg v && v.Id == vreg;

    // A physreg is free for a vreg iff the vreg's interval overlaps neither the
    // physreg's precoloured busy window (call clobbers, flag defs, livein args —
    // all in PhysRegIntervals) nor any vreg already assigned to it.
    private bool IsInterferenceFree(int vreg, int phys)
    {
        if (_intervals.Overlaps(vreg, phys)) return false;
        if (_assignedTo.TryGetValue(phys, out var occupants))
            foreach (var other in occupants)
                if (_intervals.Interfere(vreg, other))
                    return false;
        return true;
    }

    private void Commit(int vreg, int phys)
    {
        _assignment[vreg] = phys;
        if (!_assignedTo.TryGetValue(phys, out var list))
            _assignedTo[phys] = list = [];
        list.Add(vreg);
    }

    // -------------------------------------------------------------------------
    // tryEvict — STUB (Stage 1). Evicting a lighter-weight interference to free
    // a register, with cascade loop-prevention, lands here.
    // -------------------------------------------------------------------------
    private int? TryEvict(int vreg) => null;

    // -------------------------------------------------------------------------
    // trySplit — STUB (Stage 2/3). The SplitEditor analogue + tryLocalSplit /
    // split-around-call land here. Returning false sends the value to spill,
    // where the pass's existing relief (remat / TrySplitToRegister / store-
    // reload) still applies — so the skeleton is correct, just not yet splitting.
    // -------------------------------------------------------------------------
    private bool TrySplit(int vreg) => false;

    // -------------------------------------------------------------------------
    // Stage map + priority.
    // -------------------------------------------------------------------------
    private LiveRangeStage GetStage(int vreg) =>
        _stages.TryGetValue(vreg, out var s) ? s : LiveRangeStage.New;

    private void SetStage(int vreg, LiveRangeStage stage) => _stages[vreg] = stage;

    // Priority key: (-size, id) so PriorityQueue (min-first) dequeues the
    // largest interval first, ties broken by ascending vreg id.
    private (long, int) Priority(int vreg) => (-IntervalSize(vreg), vreg);

    // Total covered slots across all segments (interval "size", à la LLVM).
    private long IntervalSize(int vreg)
    {
        long size = 0;
        foreach (var seg in _intervals.IntervalOf(vreg).Segments)
            size += seg.End - seg.Start;
        return size;
    }
}

// Allocation stage of a live range (subset of LLVM's LiveRangeStage). Persisted
// across rounds by RegisterAllocatorPass so deferrals and split products keep
// their stage. New → Split (deferred / split-eligible) → Spill.
internal enum LiveRangeStage
{
    New = 0,
    Split = 1,
    Spill = 2,
}
