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
//   tryEvict   — REAL (Stage 1). Evict lighter-spill-weight committed
//                interferences to free a register, with cascade loop-prevention
//                so an A-evicts-B-evicts-A cycle cannot form. Evicted values are
//                re-enqueued and re-processed in the same Run.
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

    // The pass's spill-loop round number, threaded in purely so the gated RA
    // trace (RaTrace) can label its per-round output. Has no effect on allocation.
    private readonly int _round;

    // Per-vreg allocation stage, OWNED by the pass and shared across rounds.
    private readonly Dictionary<int, LiveRangeStage> _stages;

    // Split products (instruction-split reconciliation temps), OWNED by the pass
    // and shared across rounds so a product is never itself re-split.
    private readonly ISet<int> _splitProducts;

    // The target's callee-saved registers — the relocation home for a value split
    // around a call (a value held in one of these is preserved by the function's
    // prologue/epilogue). Snapshotted once from the register info.
    private readonly int[] _calleeSaved;

    // The target's cheap-transfer real GPRs, in preference order — the relocation
    // home preferred ahead of the zero-page pool for split products (Stage R2; see
    // PreferenceOrder). Snapshotted once from the register info.
    private readonly int[] _shortRangeGprs;

    // The committed colouring built up over one Run.
    private readonly Dictionary<int, int> _assignment = [];
    // Reverse index: physreg → vregs already assigned to it (the LiveRegMatrix
    // analogue, kept as a vreg list so interference is an interval overlap test
    // against each — precoloured physreg busy windows are queried directly off
    // _intervals.PhysRegIntervals via LiveIntervals.Overlaps).
    private readonly Dictionary<int, List<int>> _assignedTo = [];

    // Class-intersection allowed registers per vreg (preference-ordered).
    private Dictionary<int, int[]> _allowed = [];

    // Approximate per-block execution frequency (10^loopDepth), driving spill
    // weights. Computed once per Run from natural loops (CFG back-edges).
    private Dictionary<MirBlock, double> _blockFrequency = [];

    // Memoized spill weights (use/def density × block frequency ÷ interval size).
    private readonly Dictionary<int, double> _spillWeights = [];

    // Eviction loop-prevention (RegAllocGreedy.h `Cascade`). A vreg's cascade is
    // assigned the first time it evicts; each value it evicts inherits that
    // cascade, so it can only ever be evicted back by a STRICTLY newer cascade.
    // This breaks A-evicts-B-evicts-A cycles. Cascades are Run-local: every Run
    // is a fresh allocation over freshly-analysed intervals.
    private readonly Dictionary<int, int> _cascade = [];
    private int _nextCascade = 1;

    // Actual spills this round (a value that reached the spill rung). Read by the
    // pass, which rewrites them to memory traffic before re-running.
    private readonly List<int> _actualSpills = [];
    public IReadOnlyList<int> SpilledVregs => _actualSpills;

    // True when this Run edited the IR by SPLITTING (relocating a value) rather
    // than spilling — i.e. it returned null with SpilledVregs empty. The pass uses
    // this to know a split round happened.
    public bool DidSplit { get; private set; }

    // True when the split this Run performed was a RELOCATION across a clobber
    // (constrained-def-result / incumbent / relocatable-phys-clobber) — the kinds
    // whose job is to remove a constrained value's across-clobber liveness, and
    // the ONLY kinds that could in principle loop (the Mode-B failure). The pass
    // polices the well-founded measure only on these rounds; the instruction /
    // local / around-call kinds terminate by their own construction (split
    // products are never re-split, and each shortens a range or removes a
    // narrowing use).
    public bool LastSplitWasRelocation { get; private set; }

    // Well-founded termination measure (Stage R2): the COUNT of constrained,
    // non-split-product vregs that are still live across a clobber of their pinned
    // register. A vreg is "constrained" iff its allowed set is a single register R
    // (it can live only in R); it "spans a clobber" iff some point strictly after
    // its producing def — where R is busy (a precoloured physreg window OR another
    // value's def of R) — is covered by its interval. That is EXACTLY the set of
    // across-clobber relocations still to perform. Each relocation split removes
    // one such span (RelocateAcrossClobber redirects the donor's across-clobber
    // use, so the donor no longer spans it), so this count strictly decreases and
    // is bounded below by zero — a genuine well-founded measure, and (unlike a
    // raw slot-length total) INSERTION-INVARIANT: inserting a relocation copy
    // shifts slot numbers but cannot turn a resolved span back into an unresolved
    // one. The pass asserts the strict decrease on relocation rounds, so a
    // non-shrinking relocation fails loudly at its cause, not at the round cap.
    public long ConstrainedRangeMeasure { get; private set; }

    public GreedyRegisterAllocator(
        MirFunction function, TargetRegisterInfo registerInfo, LiveIntervals intervals,
        Dictionary<int, LiveRangeStage> stages, ISet<int> splitProducts,
        IReadOnlySet<int>? unspillable = null, int round = 0)
    {
        _function = function;
        _registerInfo = registerInfo;
        _intervals = intervals;
        _stages = stages;
        _splitProducts = splitProducts;
        _calleeSaved = registerInfo.GetCalleeSavedRegisters().ToArray();
        _shortRangeGprs = registerInfo.GetShortRangeGprPreference().ToArray();
        _unspillable = unspillable ?? new HashSet<int>();
        _round = round;
    }

    // Returns the final vreg→physreg colouring, or null if the engine edited the
    // IR (a spill — or, in later stages, a split). On null, SpilledVregs lists
    // any spilled vregs the pass must rewrite before re-running.
    public Dictionary<int, int>? Run()
    {
        var vregs = RegisterAllocationSupport.CollectReferencedVregs(_function);
        _allowed = RegisterAllocationSupport.ComputeAllowedColours(_function, _registerInfo, vregs);
        ExcludeHardClaimedAcrossRegisters(vregs);
        _blockFrequency = ComputeBlockFrequencies();
        ConstrainedRangeMeasure = ComputeConstrainedRangeMeasure(vregs);

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
            if (stage != LiveRangeStage.Split && TryEvict(vreg, queue) is int evicted)
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
            {
                DidSplit = true;
                RaTrace.Split(_function.Name, _round, vreg, _lastSplitKind ?? "?");
                RaTrace.DumpFunction(_function.Name, _round, _function, "post-split IR");
                return null; // IR edited → pass reanalyses and re-runs.
            }

            // 5. Spill. The pass rewrites it (remat / split-to-register /
            //    store-reload) and re-runs us with fresh intervals.
            RaTrace.Spill(_function.Name, _round, vreg, ClassNameOf(vreg));
            RaTrace.Note(_function.Name, _round,
                $"  %{vreg} allowed={{{string.Join(",", _allowed[vreg])}}} stage={stage} " +
                $"segments=[{string.Join(",", _intervals.IntervalOf(vreg).Segments.Select(s => $"{s.Start}..{s.End}"))}]");
            _actualSpills.Add(vreg);
            return null;
        }

        return _assignment;
    }

    // -------------------------------------------------------------------------
    // Across-clobber correctness exclusion (Stage R1).
    //
    // A single-physreg-pinned value W (allowed == {R}, e.g. an `ac` adc result)
    // can ONLY live in R: it has a HARD claim on R at its def-point. Any OTHER
    // value V whose live range COVERS W's def-point (V is live ACROSS the
    // re-clobber of R that W's def is) therefore must NOT be assigned R — if it
    // were, W would have nowhere to go and would spill, which the reanalysis loop
    // cannot relieve (W re-pins to R every round). This is exactly the carry-chain
    // Mode-A hazard: a relocation temp carrying the earlier adc result across the
    // next adc's $a def is copy-hinted back onto $a, evicting the final adc result
    // off its only register.
    //
    // The interference between V and W is already exact (they overlap at R); this
    // is a PREFERENCE/ALLOWED correctness narrowing that makes the ordering hazard
    // impossible — V can never grab R before W is even processed. It mirrors
    // llvm-mos forcing the across-clobber piece off R through the clobber's
    // interference window. We only remove R when V has SOME other register left
    // (so a genuinely {R}-only value is never emptied — that conflict stays for
    // the spill rung to surface). Deterministic: vregs iterated ascending.
    // -------------------------------------------------------------------------
    private void ExcludeHardClaimedAcrossRegisters(IReadOnlyList<int> vregs)
    {
        // Hard claimants of each register R: vregs whose allowed set is exactly
        // {R}, paired with their def-point slot. Built once, iterated deterministic.
        var hardClaims = new List<(int Reg, int DefSlot)>();
        foreach (var w in vregs)
        {
            if (!_allowed.TryGetValue(w, out var wa) || wa.Length != 1) continue;
            var def = _function.GetDefinition(w);
            if (def == null) continue;
            hardClaims.Add((wa[0], LiveIntervals.DefSlot(_intervals.BaseSlotOf[def])));
        }
        if (hardClaims.Count == 0) return;

        foreach (var v in vregs)
        {
            if (!_allowed.TryGetValue(v, out var va) || va.Length <= 1) continue;
            var interval = _intervals.IntervalOf(v);
            if (interval.IsEmpty) continue;

            // Registers V must yield: some other value hard-claims R at a def-point
            // strictly inside V's live range (V is live across that re-clobber).
            var excluded = new HashSet<int>();
            foreach (var (reg, defSlot) in hardClaims)
            {
                if (!RegisterAllocationSupport.Contains(va, reg)) continue;
                if (defSlot >= interval.End) continue;          // V dies at/before it.
                if (!interval.Covers(defSlot)) continue;        // V not live across it.
                // The hard claimant might be V's own def (a {R}-pinned V is handled
                // by the va.Length <= 1 guard above, so here va.Length > 1 and V is
                // not the claimant). Exclude R.
                excluded.Add(reg);
            }
            if (excluded.Count == 0) continue;

            var narrowed = va.Where(r => !excluded.Contains(r)).ToArray();
            // Never empty a value's allowed set — leave at least the original if the
            // exclusion would remove everything (a genuinely infeasible conflict the
            // spill rung must surface, not one we mask here).
            if (narrowed.Length > 0)
                _allowed[v] = narrowed;
        }
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
    // end of a pseudo.copy that is already a physreg or already assigned), then —
    // for a RELOCATION split product — the real GPRs ($a/$x/$y) ahead of the
    // zero-page pool, then the class-intersection allowed order.
    //
    // The GPR-ahead-of-zp step (Stage R2) is the copy-cost preference that makes a
    // relocation match the llvm-mos `tay`/`tya` shape. Reference: llvm-mos
    // MOSRegisterInfo::getRegAllocationHints, which scores each candidate by the
    // COPY COST across the value's copies — a relocation temp escaping $a (`%t =
    // copy <$a value>` … `$a = copy %t`) is cheapest in a real GPR ($a↔$y is
    // `tay`/`tya`: 1 byte / 2 cycles each) and dearer in zero page ($a↔zp is
    // `sta`/`lda`: 2 bytes / 3 cycles each). Both Irie's `Anyi8` order and
    // llvm-mos's put the zp pool first as the GENERAL default (zp is abundant), so
    // without this nudge the relocation lands in zp. We apply the GPR preference
    // ONLY to split products (the relocation temps) so the general flexible-value
    // placement — which deliberately favours zp to keep the three GPRs free — is
    // unchanged. A split product's whole purpose is to carry a value across a
    // clobber via the cheapest move, so a free GPR is the right home for it.
    private IEnumerable<int> PreferenceOrder(int vreg)
    {
        var allowed = _allowed[vreg];
        var emitted = new HashSet<int>();

        foreach (var hint in CopyHintColours(vreg))
            if (RegisterAllocationSupport.Contains(allowed, hint) && emitted.Add(hint))
                yield return hint;

        if (_splitProducts.Contains(vreg))
            foreach (var gpr in _shortRangeGprs)
                if (RegisterAllocationSupport.Contains(allowed, gpr) && emitted.Add(gpr))
                    yield return gpr;

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
        RaTrace.Assign(_function.Name, _round, vreg, phys, _registerInfo.GetRegisterName(phys));
    }

    // Undo a commit (used when a vreg is evicted): drop it from the colouring and
    // the reverse index so its register reads as free for the evictor.
    private void Uncommit(int vreg)
    {
        if (!_assignment.TryGetValue(vreg, out var phys)) return;
        _assignment.Remove(vreg);
        _assignedTo[phys].Remove(vreg);
    }

    // -------------------------------------------------------------------------
    // tryEvict — free a physreg for `vreg` by evicting the committed values
    // occupying it, provided every one of them is (a) lighter spill-weight than
    // `vreg`, (b) evictable (spillable + an older/no cascade — loop-prevention),
    // and (c) not a fixed precoloured interference. Among the candidate physregs
    // that satisfy this, pick the one whose evicted set is cheapest (ties broken
    // by preference order, so the copy-hint reg wins). The evicted values are
    // re-enqueued for re-allocation in the same Run.
    //
    // Reference: RegAllocGreedy.cpp tryEvict / evictInterference and
    // RegAllocEvictionAdvisor.cpp canEvictInterferenceBasedOnCost.
    // -------------------------------------------------------------------------
    private int? TryEvict(int vreg, PriorityQueue<int, (long, int)> queue)
    {
        var myWeight = SpillWeight(vreg);
        // The cascade `vreg` WOULD evict under: its own if it has one, else the
        // next-to-be-assigned (so a value that never evicted can evict anything).
        var myCascade = CascadeOrNext(vreg);

        int? best = null;
        var bestCost = double.PositiveInfinity;
        List<int>? bestVictims = null;

        foreach (var candidate in PreferenceOrder(vreg))
        {
            // A precoloured interference (call clobber, flag def, livein) is
            // fixed — it cannot be evicted, so this physreg is unusable.
            if (_intervals.Overlaps(vreg, candidate)) continue;
            if (!_assignedTo.TryGetValue(candidate, out var occupants)) continue;

            if (!TryGatherEvictees(vreg, myWeight, myCascade, candidate, occupants,
                    out var victims, out var cost))
                continue;

            if (cost < bestCost)
            {
                (best, bestCost, bestVictims) = (candidate, cost, victims);
            }
        }

        if (best is null) return null;

        // Commit the eviction: `vreg` claims (or keeps) a cascade and stamps it on
        // every evictee, then each evictee is uncommitted and re-enqueued.
        var cascade = AssignCascade(vreg);
        foreach (var victim in bestVictims!)
        {
            Uncommit(victim);
            _cascade[victim] = cascade;
            queue.Enqueue(victim, Priority(victim));
        }
        return best;
    }

    // Can `vreg` evict every interfering occupant of `candidate` (one physreg)?
    // Gathers the evictees (only the occupants that actually interfere) and their
    // summed spill weight, or returns false the moment one occupant is
    // un-evictable.
    //
    // Eviction-by-cost (Stage R2). Reference: RegAllocEvictionAdvisor.cpp
    // canEvictInterferenceBasedOnCost. The default gate is "strictly lighter
    // spill weight" (shouldEvict's `A.weight() > B.weight()`). But llvm-mos adds a
    // PREEMPTIVE path: if the FAILING value `vreg` has only ONE option for this
    // assignment (`getNumRegs() == 1` and cannot widen), then at least one
    // interference MUST be evicted for `vreg` to be placeable at all — so it may
    // evict an incumbent regardless of weight, PROVIDED that incumbent can be
    // REASSIGNED to some other free register (`canReassign`). This is exactly the
    // carry-chain shape: the failing value is pinned to a single register and the
    // incumbent holding it is cheaply relocatable. Cascade loop-prevention still
    // applies (an equal-or-newer cascade is never evicted), so this cannot cycle.
    private bool TryGatherEvictees(
        int vreg, double myWeight, int myCascade, int candidate, List<int> occupants,
        out List<int> victims, out double cost)
    {
        victims = [];
        cost = 0;
        // `vreg` has a single allowed register iff its allowed set is size 1: it
        // cannot widen (the allowed set is already the class-intersection), so any
        // interferer on `candidate` must be evicted for `vreg` to place at all.
        var oneOption = _allowed.TryGetValue(vreg, out var va) && va.Length == 1;

        foreach (var other in occupants)
        {
            if (!_intervals.Interfere(vreg, other)) continue; // doesn't block us.

            // Unspillable values (spill/remat/reconciliation temps) cannot be
            // moved; a value with an equal-or-newer cascade must not be evicted
            // (loop-prevention) — both are hard stops regardless of cost.
            if (_unspillable.Contains(other)) { victims = []; return false; }
            if (CascadeOf(other) >= myCascade) { victims = []; return false; }

            // The default policy: only evict a strictly-lighter value. The
            // preemptive exception: when `vreg` has one option and `other` can be
            // reassigned off `candidate` to a free register, evict it regardless of
            // weight (the only way `vreg` places). Mirrors the
            // `getNumRegs()==1 && canReassign` `continue` in
            // canEvictInterferenceBasedOnCost.
            if (SpillWeight(other) >= myWeight
                && !(oneOption && CanReassign(other, candidate)))
            {
                victims = []; return false;
            }

            victims.Add(other);
            cost += SpillWeight(other);
        }
        return victims.Count > 0;
    }

    // canReassign (RegAllocGreedy.cpp): is there some OTHER register (≠ fromReg)
    // in `vreg`'s allowed set that is free across `vreg`'s whole range — i.e. it
    // interferes with neither that register's precoloured busy window nor any
    // vreg already committed to it (excluding `vreg` itself)? If so, evicting
    // `vreg` off `fromReg` is harmless: it can immediately retake another register
    // when re-enqueued.
    private bool CanReassign(int vreg, int fromReg)
    {
        if (!_allowed.TryGetValue(vreg, out var allowed)) return false;
        foreach (var reg in allowed)
        {
            if (reg == fromReg) continue;
            if (_intervals.Overlaps(vreg, reg)) continue;
            var free = true;
            if (_assignedTo.TryGetValue(reg, out var occ))
                foreach (var o in occ)
                    if (o != vreg && _intervals.Interfere(vreg, o)) { free = false; break; }
            if (free) return true;
        }
        return false;
    }

    private int CascadeOf(int vreg) => _cascade.GetValueOrDefault(vreg, 0);

    private int CascadeOrNext(int vreg) =>
        _cascade.TryGetValue(vreg, out var c) ? c : _nextCascade;

    private int AssignCascade(int vreg)
    {
        if (!_cascade.TryGetValue(vreg, out var c))
            _cascade[vreg] = c = _nextCascade++;
        return c;
    }

    // -------------------------------------------------------------------------
    // trySplit (Stage 2) — try the live-range split kinds, cheapest first, via
    // the SplitEditor. Returns true if it edited the IR (the caller then returns
    // null so the pass reanalyses over fresh intervals and re-runs us):
    //
    //   1. Instruction split (split-to-register) — a copy-defined value used at
    //      narrowing single-physreg uses is split so the value widens to the
    //      flexible class and only the per-use temps need the scarce register.
    //   1b. Split-around-clobber (constrained-def-result) — a value pinned to a
    //      single physreg, live across a later re-definition of that same physreg
    //      (e.g. an `ac` adc result live across the next adc's $a def in an i16/i32
    //      chain), is relocated off the constrained register into the flexible
    //      class (zero page) right after its def. The on-demand analogue of the
    //      colourer's InsertRelocationCopiesForConstrainedDefs / the deleted isel
    //      out-funnel.
    //   1c. Split-around-clobber, INCUMBENT direction — when the FAILING value is
    //      the re-clobbering def (the second adc's $a result), the across-clobber
    //      value live over it (the incumbent already holding $a) is the one that
    //      must move. The failing value can neither split itself nor evict the
    //      heavier incumbent, so we relocate the incumbent. This is the common
    //      carry-chain shape: the longer across-clobber value wins $a first, so it
    //      is the re-clobbering newcomer that fails and reaches here.
    //   2. Local split — a value that fits no single register over its whole
    //      range, but whose pieces can each take a different free register, is cut
    //      at an interior use boundary (we supply the busy test from our committed
    //      assignment + the precoloured physreg windows).
    //   3. Split-around-call — a value live across a call whose class is entirely
    //      caller-saved (so the call's clobber barrier covers every allowed
    //      register) is relocated into a flexible-class temp across the call, so
    //      reanalysis homes the temp in a callee-saved register (the clobber
    //      windows force it there) and PrologueEpilogueInsertionPass saves it.
    //
    // If none applies, return false and the value falls through to spill, where
    // the pass's store/reload still applies. (Reference: RegAllocGreedy.cpp
    // trySplit → tryInstructionSplit / tryLocalSplit / tryRegionSplit.)
    // -------------------------------------------------------------------------
    // Which split kind last fired (for the gated RA trace only). No effect on
    // allocation; set by TrySplit just before it returns true.
    private string? _lastSplitKind;

    private bool TrySplit(int vreg)
    {
        LastSplitWasRelocation = false;
        var editor = new SplitEditor(_function, _registerInfo);
        if (editor.TryInstructionSplit(vreg, _splitProducts)) { _lastSplitKind = "instruction"; return true; }
        // Split-around-clobber: a value pinned to a single physreg that is live
        // across a later re-definition of that same physreg (e.g. an `ac` adc
        // result live across the next adc in an i16/i32 chain) is relocated off
        // the constrained register into the flexible class. Placed before local /
        // split-around-call because it is the COMMON constrained-chain case and is
        // a cheap def-site relocation (no busy-test callback needed) — the others
        // handle free-register gaps and call barriers respectively.
        if (editor.TrySplitConstrainedDefResult(vreg, _intervals, _allowed, _splitProducts))
        { _lastSplitKind = "constrained-def-result"; LastSplitWasRelocation = true; return true; }
        // Split-around-clobber, INCUMBENT direction: when the failing value is the
        // re-clobbering def itself (the second adc's $a result), the value that must
        // move is the OTHER one — the incumbent live across this def, already holding
        // $a. The failing value can neither split itself (it is not live across a
        // later $a def) nor evict the heavier incumbent, so without this it spills
        // uselessly. Relocate the incumbent instead. (1b handles the case where the
        // across-clobber value is itself the failing vreg; 1c the more common case
        // where the re-clobbering value fails first.)
        if (editor.TrySplitIncumbentAcrossClobber(vreg, _intervals, _allowed, _splitProducts))
        { _lastSplitKind = "incumbent-across-clobber"; LastSplitWasRelocation = true; return true; }
        // Split-not-spill across a PRECOLOURED physreg clobber (Stage R2,
        // RS_LightSpill analogue): a {R}-pinned, flexible-superclass value live
        // across a direct physreg def of R (e.g. the high adc result live across
        // the ABI return move `$a = copy <low byte>`). Relocate it off R rather
        // than letting it fall to the memory-spill rung — R is a flexible-class
        // member, so the value is genuinely relocatable. Placed after the vreg-
        // re-clobber kinds (those catch the common chain shape) and before local /
        // around-call, mirroring the constrained-def-result ordering.
        if (editor.TrySplitRelocatableAcrossPhysClobber(vreg, _intervals, _allowed, _splitProducts))
        { _lastSplitKind = "relocatable-across-phys-clobber"; LastSplitWasRelocation = true; return true; }
        if (editor.TryLocalSplit(vreg, _intervals, _allowed[vreg], RegisterBusyAcross))
        { _lastSplitKind = "local"; return true; }
        // Split-around-call. The minted relocation temp is recorded in
        // _splitProducts (so it is never itself re-split); the source's range is
        // shortened to end at the pre-call copy, so it no longer lives across the
        // call and FindAcrossCallBarrier won't pick it again — termination is by
        // range shortening, not a flag.
        if (editor.TrySplitAroundCall(
            vreg, _intervals, _allowed[vreg], _calleeSaved, _splitProducts, RegisterBusyAcross))
        { _lastSplitKind = "around-call"; return true; }
        return false;
    }

    // The printable register-class name of a vreg (for the gated spill trace).
    private string ClassNameOf(int vreg) =>
        _function.GetVRegAnnotation(vreg) is ClassedVReg c ? c.Name : "?";

    // Is `reg` busy anywhere in the half-open slot window [start, end)? Busy means
    // a precoloured physreg window (call clobber, flag def, livein) covers a point
    // there, or some vreg already COMMITTED to `reg` this Run is live there. This
    // is the contention signal local split needs; only the allocator has the
    // committed-assignment picture, so we supply it as a callback.
    private bool RegisterBusyAcross(int reg, int start, int end)
    {
        var phys = _intervals.PhysIntervalOf(reg);
        for (var p = start; p < end; p++)
            if (phys.Covers(p)) return true;

        if (_assignedTo.TryGetValue(reg, out var occupants))
            foreach (var other in occupants)
            {
                var interval = _intervals.IntervalOf(other);
                for (var p = start; p < end; p++)
                    if (interval.Covers(p)) return true;
            }
        return false;
    }

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

    // The well-founded termination measure (Stage R2): the COUNT of constrained,
    // non-split-product vregs still live across a clobber of their single pinned
    // register. See the ConstrainedRangeMeasure property doc for why this is
    // well-founded and insertion-invariant. Deterministic (vregs iterated in the
    // CollectReferencedVregs order, which is ascending by construction).
    private long ComputeConstrainedRangeMeasure(IReadOnlyList<int> vregs)
    {
        long measure = 0;
        foreach (var v in vregs)
        {
            if (_splitProducts.Contains(v)) continue;            // a split-minted temp.
            if (!_allowed.TryGetValue(v, out var a) || a.Length != 1) continue;
            if (SpansClobberOfPinnedReg(v, a[0])) measure++;
        }
        return measure;
    }

    // Does constrained vreg `v` (allowed == {R}) live across a clobber of R — a
    // point strictly after `v`'s producing def where R is busy (a precoloured
    // physreg window OR another value's def of R) and `v`'s interval covers it?
    // This is the predicate the relocation splits exist to clear.
    private bool SpansClobberOfPinnedReg(int v, int reg)
    {
        var interval = _intervals.IntervalOf(v);
        if (interval.IsEmpty) return false;
        var def = _function.GetDefinition(v);
        // Multi-def (two-address): use the latest def-point so the across-clobber
        // window is measured from where the live value is produced.
        var defPoint = long.MinValue;
        foreach (var block in _function.Blocks)
            foreach (var instr in block.Instructions)
                if (DefinesVreg(instr, v))
                {
                    var s = LiveIntervals.DefSlot(_intervals.BaseSlotOf[instr]);
                    if (s > defPoint) defPoint = s;
                }
        if (def == null && defPoint == long.MinValue) return false;

        // A precoloured R window opening strictly after the def and covered by `v`.
        foreach (var seg in _intervals.PhysIntervalOf(reg).Segments)
            if (seg.Start > defPoint && seg.Start < interval.End && interval.Covers(seg.Start))
                return true;

        // Another vreg pinned {R} whose def-point `v` lives across.
        foreach (var w in RegisterAllocationSupport.CollectReferencedVregs(_function))
        {
            if (w == v) continue;
            if (!_allowed.TryGetValue(w, out var wa) || wa.Length != 1 || wa[0] != reg) continue;
            var wdef = _function.GetDefinition(w);
            if (wdef == null) continue;
            var wp = LiveIntervals.DefSlot(_intervals.BaseSlotOf[wdef]);
            if (wp > defPoint && wp < interval.End && interval.Covers(wp))
                return true;
        }
        return false;
    }

    private static bool DefinesVreg(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (op is VirtualReg d && d.IsDefinition && d.Id == vreg) return true;
        return false;
    }

    // -------------------------------------------------------------------------
    // Spill weight — how costly it is to evict/spill a value, so eviction picks
    // the lighter victim. Mirrors LLVM's VirtRegAuxInfo: sum the block frequency
    // at each reference (def/use) and divide by the interval size, so a value
    // that is used often / in hot (looping) blocks resists eviction, while a long
    // lightly-used range is cheap to evict and reallocate. Unspillable values
    // (spill/remat/reconciliation temps) are infinitely heavy — never evicted.
    // Memoized: weights don't change within a Run.
    // -------------------------------------------------------------------------
    private double SpillWeight(int vreg)
    {
        if (_unspillable.Contains(vreg)) return double.PositiveInfinity;
        if (_spillWeights.TryGetValue(vreg, out var cached)) return cached;

        double total = 0;
        foreach (var block in _function.Blocks)
        {
            var freq = _blockFrequency.GetValueOrDefault(block, 1.0);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is VirtualReg v && v.Id == vreg)
                        total += freq;
        }

        var weight = total / Math.Max(1, IntervalSize(vreg));
        _spillWeights[vreg] = weight;
        return weight;
    }

    // -------------------------------------------------------------------------
    // Approximate block frequencies as 10^loopDepth, where loop depth comes from
    // the natural loops of the CFG (a block's depth = how many natural-loop
    // bodies contain it). This is a deliberately COARSE proxy for LLVM's
    // BlockFrequencyInfo — Irie has no profile/branch-probability data — but it
    // captures the property that matters for eviction ordering: values in loops
    // are hotter and should resist eviction. Refine if a corpus case demands it.
    // -------------------------------------------------------------------------
    private Dictionary<MirBlock, double> ComputeBlockFrequencies()
    {
        var depth = ComputeLoopDepths();
        var freq = new Dictionary<MirBlock, double>(depth.Count);
        foreach (var (block, d) in depth)
            freq[block] = Math.Pow(10, d);
        return freq;
    }

    // Loop depth per block via natural loops: find CFG back-edges (an edge to a
    // block still on the DFS recursion stack), then for each back-edge tail→head
    // mark every block in the loop body — head plus all blocks that can reach the
    // tail without passing through the head — incrementing their depth.
    private Dictionary<MirBlock, int> ComputeLoopDepths()
    {
        _function.RebuildCfg();
        var depth = new Dictionary<MirBlock, int>(_function.Blocks.Count);
        foreach (var block in _function.Blocks)
            depth[block] = 0;
        if (_function.Blocks.Count == 0) return depth;

        // DFS from the entry block, colouring nodes white(0)/grey(1)/black(2).
        // A grey successor is a back-edge target (an ancestor on the stack).
        var colour = new Dictionary<MirBlock, int>();
        var backEdges = new List<(MirBlock Tail, MirBlock Head)>();
        var stack = new Stack<(MirBlock Node, int Child)>();
        stack.Push((_function.Blocks[0], 0));
        colour[_function.Blocks[0]] = 1;
        while (stack.Count > 0)
        {
            var (node, child) = stack.Pop();
            if (child < node.Successors.Count)
            {
                stack.Push((node, child + 1));
                var succ = node.Successors[child];
                var c = colour.GetValueOrDefault(succ, 0);
                if (c == 0)
                {
                    colour[succ] = 1;
                    stack.Push((succ, 0));
                }
                else if (c == 1)
                {
                    backEdges.Add((node, succ)); // grey → back-edge.
                }
            }
            else
            {
                colour[node] = 2;
            }
        }

        // Each back-edge defines a natural loop; bump every body block's depth.
        foreach (var (tail, head) in backEdges)
            foreach (var body in NaturalLoopBody(tail, head))
                depth[body]++;

        return depth;
    }

    // The natural loop of back-edge tail→head: {head} plus every block that can
    // reach `tail` walking predecessors backwards without passing through `head`.
    private static HashSet<MirBlock> NaturalLoopBody(MirBlock tail, MirBlock head)
    {
        var body = new HashSet<MirBlock> { head };
        var work = new Stack<MirBlock>();
        if (body.Add(tail)) work.Push(tail);
        while (work.Count > 0)
            foreach (var pred in work.Pop().Predecessors)
                if (body.Add(pred))
                    work.Push(pred);
        return body;
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
