using Irie.Mir;

namespace Irie.Passes.Analyses;

// =============================================================================
// LiveIntervalsAnalysis — builds the physreg-aware LiveIntervals (with holes).
// =============================================================================
//
// This analysis is the Phase-1 foundation of the register-allocator redesign
// (notes/register-allocator-redesign-plan.md §3.1). It REPLACES the coarse
// vreg-only LivenessAnalysis — but only in Phase 2; this phase merely stands it
// up alongside the old one and proves it reproduces the old pass's ad-hoc
// physreg-interference conclusions (ComputeClobberSlots /
// ComputePhysRegReservations). It does NOT yet change RegisterAllocatorPass.
//
// Algorithm (Appel ch. 11; same backward dataflow as the old analysis, but
// computing precise segments instead of one [start,end] per value):
//
//   1. Number every instruction with a base sub-slot (SlotsPerInstruction apart)
//      so a use point and a def point can be addressed separately
//      (LiveIntervals header explains why).
//   2. Backward dataflow over vregs to get per-block LiveIn/LiveOut sets
//      (LiveOut[B] = ∪ LiveIn[succ]; LiveIn[B] = use[B] ∪ (LiveOut[B] − def[B])).
//      Same equations as LivenessAnalysis — verified against it.
//   3. For each block, build precise *local* segments for each value (vreg AND
//      physreg) by walking instructions and pairing each def with the uses that
//      follow it, with block boundaries closed by the live-in/live-out sets.
//      A value live-in to a block opens a segment at the block's first use
//      point; a value live-out closes at the block's last def point.
//   4. Normalize each interval (sort + merge), yielding holes wherever a value
//      went dead and was later redefined within a block or across blocks.
//
// PHYSREGS are tracked exactly like vregs, reading them out of the operand
// array: by the time RA runs, the instruction selector and call lowering have
// already materialized every implicit def/use (call clobbers, flag defs such as
// mos6502.cmp's $n/$z/$c) as explicit PhysicalReg(IsImplicit:true) operands.
// So "what physreg does this instruction clobber / read" is answered by reading
// PhysicalReg operands — the same source ComputeClobberSlots/Reservations read —
// not by re-consulting DialectInstructionInfo.ImplicitDefs. Entry-block CC
// live-ins (MirBlock.LiveIns) seed the entry physreg segments.
public sealed class LiveIntervalsAnalysis : MirFunctionAnalysis<LiveIntervals>
{
    public override LiveIntervals Compute(MirFunction function)
    {
        function.RebuildCfg();

        var blocks = function.Blocks;

        // --- Step 1: number instructions with base sub-slots. ----------------
        // Each instruction owns SlotsPerInstruction (=2) consecutive points; the
        // use point is base+0, the def point is base+1. We also record, per
        // block, the first slot and the exclusive end slot so block-boundary
        // (live-out) segments can be anchored precisely.
        var baseSlotOf = new Dictionary<MirInstruction, int>();
        var blockFirstSlot = new Dictionary<MirBlock, int>(blocks.Count);
        // Exclusive end slot: the first slot AFTER the block (== the next block's
        // first slot, because blocks are numbered contiguously). A value that is
        // live-out of a block extends to this point so its segment abuts the
        // successor's entry segment with no spurious hole at the block boundary.
        var blockEndSlot = new Dictionary<MirBlock, int>(blocks.Count);

        var nextBase = 0;
        foreach (var block in blocks)
        {
            blockFirstSlot[block] = nextBase;
            foreach (var instr in block.Instructions)
            {
                baseSlotOf[instr] = nextBase;
                nextBase += LiveIntervals.SlotsPerInstruction;
            }
            // An empty block still consumes one slot of width so it can carry a
            // live-through segment.
            if (block.Instructions.Count == 0)
                nextBase += LiveIntervals.SlotsPerInstruction;
            blockEndSlot[block] = nextBase;
        }

        // --- Step 2: backward dataflow for vreg LiveIn / LiveOut. ------------
        var (liveIn, liveOut) = ComputeVRegDataflow(function);

        // --- Step 3: build precise segments per value. -----------------------
        // We accumulate into one builder per value (vregs keyed by id, physregs
        // keyed by id in a separate map). Each block contributes a set of
        // local segments; cross-block continuity comes from liveIn/liveOut and
        // (for physregs) per-block live-in tracking + the half-open numbering.
        var vregBuilders = new Dictionary<int, LiveInterval>();
        var physBuilders = new Dictionary<int, LiveInterval>();

        LiveInterval VRegBuilder(int id) =>
            vregBuilders.TryGetValue(id, out var b) ? b : vregBuilders[id] = new LiveInterval();
        LiveInterval PhysBuilder(int id) =>
            physBuilders.TryGetValue(id, out var b) ? b : physBuilders[id] = new LiveInterval();

        foreach (var block in blocks)
        {
            BuildVRegSegmentsForBlock(block, baseSlotOf, blockFirstSlot[block],
                blockEndSlot[block], liveIn[block], liveOut[block], VRegBuilder);

            BuildPhysRegSegmentsForBlock(block, baseSlotOf, blockFirstSlot[block],
                blockEndSlot[block], PhysBuilder);
        }

        // --- Step 4: normalize (sort + merge → holes). -----------------------
        foreach (var iv in vregBuilders.Values) iv.Normalize();
        foreach (var iv in physBuilders.Values) iv.Normalize();

        return new LiveIntervals(baseSlotOf, vregBuilders, physBuilders);
    }

    // -------------------------------------------------------------------------
    // Step 2: classic backward liveness dataflow over VIRTUAL registers only.
    // Identical equations to LivenessAnalysis (so the two agree on vreg
    // liveness); physregs are handled structurally in step 3, not by dataflow,
    // because by RA time every physreg use/def is explicit in the operand array
    // and physreg live ranges do not span blocks except via MirBlock.LiveIns.
    // -------------------------------------------------------------------------
    private static (Dictionary<MirBlock, HashSet<int>> LiveIn,
                    Dictionary<MirBlock, HashSet<int>> LiveOut)
        ComputeVRegDataflow(MirFunction function)
    {
        var blocks = function.Blocks;
        var rpo = ComputeReversePostOrder(function);

        // Per-block upward-exposed uses and killed (defined) vregs. Process uses
        // before defs within an instruction so a vreg both used and re-defined
        // by the same instruction counts as upward-exposed.
        var upwardExposed = new Dictionary<MirBlock, HashSet<int>>(blocks.Count);
        var killed = new Dictionary<MirBlock, HashSet<int>>(blocks.Count);

        foreach (var block in blocks)
        {
            var exposed = new HashSet<int>();
            var kill = new HashSet<int>();

            foreach (var param in block.Parameters)
                kill.Add(param);

            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                    foreach (var vreg in EnumerateVRegUses(op))
                        if (!kill.Contains(vreg))
                            exposed.Add(vreg);
                foreach (var op in instr.Operands)
                    if (op is VirtualReg { IsDefinition: true } v)
                        kill.Add(v.Id);
            }
            upwardExposed[block] = exposed;
            killed[block] = kill;
        }

        var liveIn = blocks.ToDictionary(b => b, _ => new HashSet<int>());
        var liveOut = blocks.ToDictionary(b => b, _ => new HashSet<int>());

        bool changed;
        do
        {
            changed = false;
            foreach (var block in rpo)
            {
                var newLiveOut = new HashSet<int>();
                foreach (var succ in block.Successors)
                    newLiveOut.UnionWith(liveIn[succ]);

                var newLiveIn = new HashSet<int>(upwardExposed[block]);
                foreach (var v in newLiveOut)
                    if (!killed[block].Contains(v))
                        newLiveIn.Add(v);

                if (!newLiveOut.SetEquals(liveOut[block]) || !newLiveIn.SetEquals(liveIn[block]))
                {
                    liveOut[block] = newLiveOut;
                    liveIn[block] = newLiveIn;
                    changed = true;
                }
            }
        }
        while (changed);

        return (liveIn, liveOut);
    }

    // -------------------------------------------------------------------------
    // Step 3a: precise per-block VREG segments.
    //
    // We walk the block once and, for each vreg, track the slot at which its
    // current live segment opened ("openAt"). A segment opens when the vreg is
    // live-in (open at the block's first use point) or defined (open at that
    // instruction's def point). A segment provisionally closes at each use (the
    // use point of the using instruction). If the vreg is live-out, its final
    // open segment is closed at the block's last def point so it reaches the
    // successor. A vreg defined, used, then redefined within the block produces
    // TWO segments with a hole between — that is the hole tracking the old
    // analysis lacked.
    //
    // Ordering note: we emit the previous segment's close *before* opening a new
    // one on a re-definition, so a value re-defined in the same block leaves a
    // hole between its last use and the re-def's def point.
    // -------------------------------------------------------------------------
    private static void BuildVRegSegmentsForBlock(
        MirBlock block,
        IReadOnlyDictionary<MirInstruction, int> baseSlotOf,
        int firstSlot,
        int endSlot,
        HashSet<int> liveIn,
        HashSet<int> liveOut,
        Func<int, LiveInterval> builder)
    {
        // For each currently-live vreg we track the slot its open segment began
        // (openAt) and the slot at which it should close so far (closeAt).
        // closeAt starts one point past the open point — a dead def
        // (defined-but-never-used) thus gets a tiny unit segment [open, open+1],
        // matching the old single-slot range [s, s] so it still interferes with
        // anything overlapping its def. Each real use raises closeAt to that
        // use's point; live-out at block end raises it to the block's last def
        // point so the segment abuts the successor.
        var openAt = new Dictionary<int, int>();
        var closeAt = new Dictionary<int, int>();

        var firstUsePoint = LiveIntervals.UseSlot(firstSlot);

        // Block parameters and live-in vregs are live from block entry. Open
        // them at the block's first use point so a segment exists even if the
        // value is only passed straight through.
        foreach (var v in block.Parameters)
        {
            openAt[v] = firstUsePoint;
            closeAt[v] = firstUsePoint;
        }
        foreach (var v in liveIn)
        {
            if (openAt.ContainsKey(v)) continue;
            openAt[v] = firstUsePoint;
            closeAt[v] = firstUsePoint;
        }

        foreach (var instr in block.Instructions)
        {
            var b = baseSlotOf[instr];
            var usePoint = LiveIntervals.UseSlot(b);
            var defPoint = LiveIntervals.DefSlot(b);

            // Uses read at the use point. A live segment is half-open
            // [start, end), so to *cover* the use point P the close must be P+1
            // — which is exactly this instruction's def point. So a value's
            // segment ends at the def point of its last user; a fresh value
            // defined by that same instruction (at the def point) abuts but does
            // NOT overlap it, which is the def/use sharing the sub-slot scheme
            // exists to express.
            foreach (var op in instr.Operands)
                foreach (var v in EnumerateVRegUses(op))
                    if (openAt.ContainsKey(v))
                        closeAt[v] = defPoint;

            // Defs write at the def point. If the vreg already had an open
            // segment (it is being re-defined), flush that segment first —
            // leaving a hole between its last close point and this def. Then
            // open a fresh segment at the def point.
            foreach (var op in instr.Operands)
            {
                if (op is not VirtualReg { IsDefinition: true } def) continue;
                var v = def.Id;
                if (openAt.TryGetValue(v, out var prevOpen))
                    builder(v).Add(prevOpen, closeAt[v]);
                openAt[v] = defPoint;
                closeAt[v] = defPoint + 1; // dead-def unit until a use appears
            }
        }

        // Flush whatever is still open at block end. A live-out value extends to
        // the block's exclusive end slot (the successor's first slot) so its
        // segment abuts the successor's entry segment — they merge in Normalize,
        // giving one continuous cross-block interval with no boundary hole.
        foreach (var (v, open) in openAt)
        {
            var close = liveOut.Contains(v) ? Math.Max(endSlot, open + 1) : closeAt[v];
            builder(v).Add(open, close);
        }
    }

    // -------------------------------------------------------------------------
    // Step 3b: precise per-block PHYSREG segments.
    //
    // Physregs are read straight out of the operand array (explicit + implicit
    // PhysicalReg operands). This is the *same* information the old pass's
    // ComputeClobberSlots (implicit defs) and ComputePhysRegReservations (a copy
    // reading $P after a def of $P) reconstructed by hand — here it falls out of
    // the same def/use segment machinery as vregs.
    //
    //   - A physreg DEF (explicit or implicit) opens a segment at the def point.
    //     A def while already open closes the prior segment first (hole / new
    //     value), exactly like a vreg re-definition.
    //   - A physreg USE extends the open segment to the use point.
    //   - MirBlock.LiveIns physregs (entry-block CC args, cross-block live
    //     physregs) open at the block's first use point.
    //
    // Cross-block physreg liveness: there is no physreg dataflow here. Instead a
    // physreg that is genuinely live across a block boundary is recorded in the
    // successor's MirBlock.LiveIns (populated by AbiLowering for the entry block
    // and by RA for the rest). A physreg whose value does not escape the block
    // simply closes at its last use, which is the conservative-but-correct local
    // picture the allocator needs for interference.
    // -------------------------------------------------------------------------
    private static void BuildPhysRegSegmentsForBlock(
        MirBlock block,
        IReadOnlyDictionary<MirInstruction, int> baseSlotOf,
        int firstSlot,
        int endSlot,
        Func<int, LiveInterval> builder)
    {
        // Same openAt / closeAt model as the vreg builder (see there for the
        // dead-def unit-segment rationale).
        var openAt = new Dictionary<int, int>();
        var closeAt = new Dictionary<int, int>();

        var firstUsePoint = LiveIntervals.UseSlot(firstSlot);

        // CC / cross-block physreg live-ins are live from block entry.
        foreach (var p in block.LiveIns)
        {
            openAt[p] = firstUsePoint;
            closeAt[p] = firstUsePoint;
        }

        foreach (var instr in block.Instructions)
        {
            var b = baseSlotOf[instr];
            var usePoint = LiveIntervals.UseSlot(b);
            var defPoint = LiveIntervals.DefSlot(b);

            // Physreg uses (explicit or implicit) raise the open segment's close
            // to this instruction's def point (P+1), so the segment covers the
            // use point P under half-open semantics — see the vreg builder.
            foreach (var op in instr.Operands)
                if (op is PhysicalReg { IsDefinition: false } pu && openAt.ContainsKey(pu.Id))
                    closeAt[pu.Id] = defPoint;

            // Physreg defs (explicit or implicit) flush any open segment (a
            // clobber kills whatever value was there) and open a fresh one.
            foreach (var op in instr.Operands)
            {
                if (op is not PhysicalReg { IsDefinition: true } pd) continue;
                var p = pd.Id;
                if (openAt.TryGetValue(p, out var prevOpen))
                    builder(p).Add(prevOpen, closeAt[p]);
                openAt[p] = defPoint;
                closeAt[p] = defPoint + 1; // dead-def / clobber unit until a use
            }
        }

        // Flush anything still open at block end. There is no physreg dataflow:
        // cross-block liveness is carried by the successor's MirBlock.LiveIns.
        //
        // A live-in physreg is extended to the block's exclusive end slot ONLY
        // when it is genuinely LIVE-OUT — i.e. some successor lists it as a
        // live-in — so its segment abuts the successor's entry segment with no
        // boundary hole. A live-in physreg that is *consumed* within the block
        // (e.g. a CC-argument register read by an entry `pseudo.copy $a` and
        // then dead) must NOT be extended: its busy window ends at its last use.
        //
        // This distinction matters because closeAt cannot by itself tell "held
        // through" from "consumed and dead" — a use sets closeAt but the physreg
        // stays in openAt (openAt is only cleared on re-definition). Extending
        // every live-in to block end was over-conservative: it kept a CC arg
        // register ($a/$x) busy across the whole entry block, so a vreg pinned
        // to $a (e.g. a cmp's $a operand) could never be allocated even though
        // $a was free the instant after the argument copy. The live-out test
        // fixes that without re-introducing a boundary hole for true pass-through
        // physregs.
        foreach (var (p, open) in openAt)
        {
            var close = closeAt[p];
            if (IsLiveOut(block, p) && endSlot > close)
                close = endSlot;
            builder(p).Add(open, close);
        }
    }

    // A physreg is live-out of a block iff some successor lists it as a live-in
    // (the post-RA cross-block liveness convention; MirBlock.LiveIns is set by
    // AbiLowering for the entry block and by RA for the rest). A physreg merely
    // live-IN to the current block but read and dropped here is not live-out.
    private static bool IsLiveOut(MirBlock block, int physReg)
    {
        foreach (var succ in block.Successors)
            if (succ.LiveIns.Contains(physReg))
                return true;
        return false;
    }

    // Vreg uses, recursing into BlockTarget.Args (same helper shape as
    // LivenessAnalysis so the two analyses see uses identically).
    private static IEnumerable<int> EnumerateVRegUses(MirOperand operand)
    {
        switch (operand)
        {
            case VirtualReg { IsDefinition: false } v:
                yield return v.Id;
                break;
            case BlockTarget bt:
                foreach (var arg in bt.Args)
                    foreach (var vreg in EnumerateVRegUses(arg))
                        yield return vreg;
                break;
        }
    }

    // DFS post-order reversed = reverse post-order. Unreachable blocks appended
    // in function-list order (matches LivenessAnalysis).
    private static List<MirBlock> ComputeReversePostOrder(MirFunction function)
    {
        if (function.Blocks.Count == 0) return [];

        var visited = new HashSet<MirBlock>();
        var postOrder = new List<MirBlock>(function.Blocks.Count);

        void Dfs(MirBlock block)
        {
            if (!visited.Add(block)) return;
            foreach (var succ in block.Successors)
                Dfs(succ);
            postOrder.Add(block);
        }

        Dfs(function.Blocks[0]);

        foreach (var block in function.Blocks)
            if (!visited.Contains(block))
                postOrder.Add(block);

        postOrder.Reverse();
        return postOrder;
    }
}
