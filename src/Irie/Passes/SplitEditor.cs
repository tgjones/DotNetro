using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// SplitEditor — the mechanical core of live-range splitting (greedy RA).
// =============================================================================
//
// Reference: llvm/lib/CodeGen/SplitKit.{h,cpp} `SplitEditor`
// (openIntv / enterIntvBefore / leaveIntvAfter / useIntv / finish). This is the
// drastically-simplified Irie analogue used by GreedyRegisterAllocator.TrySplit
// (greedy-RA plan, Stage 2).
//
// ----------------------------------------------------------------------------
// Reanalysis instead of incremental liveness (the big simplification)
// ----------------------------------------------------------------------------
// LLVM's SplitEditor maintains liveness incrementally (transferValues,
// extendPHIRange, hoistCopies …) because rebuilding LiveIntervals for the whole
// function after every split would be too expensive there. Irie's allocator
// ALREADY reanalyses LiveIntervals every round of its spill/split loop, so this
// editor does NOT keep liveness up to date: it only performs the IR EDIT
// (mint a new vreg, insert the boundary `pseudo.copy`s, rewrite the in-region
// uses) and returns true. The pass then reanalyses and re-runs the allocator
// over fresh intervals, which reconstructs all liveness for free. The cost is
// one IR edit per round — acceptable at the function sizes this target sees.
//
// ----------------------------------------------------------------------------
// Split kinds (the policies that drive the editor)
// ----------------------------------------------------------------------------
//   * TryInstructionSplit — the llvm-mos `tryInstructionSplit` / "split to
//     register" analogue. A value whose def is a copy but which is consumed at
//     NARROWING uses (an operand class that is a strict subset of the flexible
//     8-bit class, e.g. `ac` = {$a}) is split by minting a fresh flexible temp
//     at each narrowing use and copying the value into it there. The value
//     itself then flows only through copies, so the next reanalysis widens it to
//     the flexible class (it can live in the abundant zero-page file) and only
//     the short per-use temps need the scarce constrained register.
//   * TryLocalSplit — gap-based split within a single block: when a value is
//     interfered across a region inside one block (its register is busy there),
//     split the live range at the widest such gap so the value vacates the
//     register across the gap and is copied back afterwards.
//
// NOTE (duplication, resolved in Stage 4): the instruction-split logic also
// exists as RegisterAllocatorPass.TrySplitToRegister, which the graph-colouring
// allocator's spill path still uses. The colourer is the DEFAULT engine during
// greedy bring-up and is left untouched to avoid risk (same discipline as the
// duplicated RegisterAllocationSupport.ComputeAllowedColours). The two converge
// when the colourer is retired in Stage 4.
internal sealed class SplitEditor(MirFunction function, TargetRegisterInfo registerInfo)
{
    // =========================================================================
    // SplitAtPoint — the value-number-precise split primitive (SplitKit S2).
    // =========================================================================
    //
    // Reference: llvm SplitKit.cpp SplitEditor open/enter/leave/use/finish, but
    // collapsed to a single IR-edit primitive (the reanalysis architecture drops
    // incremental liveness; PhiElimination already ran so there are no PHIs /
    // block args to repair — see notes/generic-relocation-pass-plan.md, "The
    // split primitive").
    //
    // This GENERALIZES RelocateAcrossClobber (above): instead of redirecting uses
    // by the program-order slot window (producingDefSlot, nextDefSlot) — which is
    // only correct on straight-line code — it redirects exactly the uses that read
    // the relocated value, determined by a forward CFG reachability walk that stops
    // at any re-def of the value. In a branching/looping CFG a use can fall inside
    // the naive slot window yet read a DIFFERENT reaching definition; the
    // reachability walk excludes it, the slot window does not. (RelocateAcrossClobber
    // is left in place; S3 rewires the ladder onto this primitive.)
    //
    // Semantics: cut `value`'s live range at the program point `at`+`side`,
    // relocating the post-point portion into a fresh vreg V' with a boundary copy
    // `V' = pseudo.copy value`, and rename every downstream use of `value` reached
    // forward from the split point (without passing another def of `value`) to V'.
    // The boundary copy's own read of `value` is the boundary and is never renamed.
    //
    //   side = Before  → split point is `at`'s USE point; copy inserted before `at`;
    //                    `at`'s own use of `value` (and all later reachable uses) move.
    //   side = After   → split point is `at`'s DEF point; copy inserted after `at`;
    //                    only strictly-later reachable uses move (`at`'s own reads
    //                    happen at the use point, before the split, and stay).
    //
    // Returns false (no edit) when `value` is not live across the split point, or
    // no relocation class is available. Returns true after editing; the caller
    // reanalyses.
    public enum InsertSide { Before, After }

    public bool SplitAtPoint(
        int value, MirInstruction at, InsertSide side,
        LiveIntervals intervals, ISet<int> splitProducts,
        int? overrideClassId = null)
    {
        var atBlock = at.Parent
            ?? throw new InvalidOperationException("SplitAtPoint: `at` has no parent block.");
        var atIndex = atBlock.Instructions.IndexOf(at);
        if (atIndex < 0)
            throw new InvalidOperationException("SplitAtPoint: `at` is not in its parent block.");

        var baseSlot = intervals.BaseSlotOf[at];
        // The slot at which the value must be live for there to be anything to
        // split. Before → the use point (`at` reads the value); After → the def
        // point (the value flows past `at`).
        var splitSlot = side == InsertSide.Before
            ? LiveIntervals.UseSlot(baseSlot)
            : LiveIntervals.DefSlot(baseSlot);

        // Step 1: the value number of `value` live across the split point. If no
        // segment covers the split slot, the value is not live there — nothing to
        // split.
        var valNo = intervals.ValNoAt(value, splitSlot);
        if (valNo == null) return false;

        // Step 2: mint the relocation vreg V'. Override class if given, else the
        // flexible 8-bit class (the relocation default). Bail if neither is usable.
        int classId;
        string className;
        if (overrideClassId is { } oc)
        {
            classId = oc;
            className = registerInfo.GetRegisterClassName(oc) ?? $"class{oc}";
        }
        else
        {
            classId = registerInfo.FlexibleI8ClassId;
            if (classId == 0) return false;
            className = registerInfo.GetRegisterClassName(classId) ?? $"class{classId}";
        }

        // Step 4 (computed BEFORE the edit so recorded references stay valid): the
        // set of (instruction, operand-index) use sites that read the relocated
        // value, via forward CFG reachability from the split point. The boundary
        // copy does not exist yet, so it cannot appear in this set — its read of
        // `value` is added by hand below and is correctly never renamed.
        var renameSites = CollectForwardReachableUses(value, atBlock, atIndex, side);

        // If the split relocates nothing downstream there is no point cutting — the
        // value already dies at/before the split point on every forward path.
        if (renameSites.Count == 0) return false;

        // Step 2/3: mint V' and insert the boundary copy at the split point.
        var prime = function.CreateVirtualRegisterInClass(classId, className);
        splitProducts.Add(prime);
        var copyIndex = side == InsertSide.Before ? atIndex : atIndex + 1;
        atBlock.InsertInstruction(
            copyIndex,
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(prime, IsDefinition: true),
            new VirtualReg(value, IsDefinition: false));

        // Step 4: rename the collected use sites to V'. Operating on held
        // (instruction, index) references is index-shift-proof: the inserted copy
        // moved instructions within atBlock, but we never recorded positions.
        foreach (var (instr, index) in renameSites)
            instr.Operands[index] = new VirtualReg(prime, IsDefinition: false);

        return true;
    }

    // -------------------------------------------------------------------------
    // The reachability core (reused by S4's tryBlockSplit): the use sites of
    // `value` reached FORWARD from the split point without passing through another
    // def of `value`. This is what makes the split value-number-correct in a
    // branching/looping CFG — a slot-order window cannot express "reached from the
    // split point", and so mis-renames sibling/back-edge uses that read a different
    // reaching def.
    //
    // The split point is "just after the boundary copy", which sits at:
    //   side = Before → before `at` (copy at atIndex): the rename region in the
    //          split block begins AT `at` (atIndex) — `at`'s own read of `value`
    //          reads the just-copied boundary value and moves.
    //   side = After  → after `at` (copy at atIndex+1): the rename region begins
    //          strictly AFTER `at` (atIndex+1) — `at`'s reads happen before the
    //          split and stay.
    //
    // Block-carry dataflow (a standard forward worklist):
    //   * The split block "carries V' out" iff no instruction in its rename region
    //     re-defines `value` (a redef there kills the relocated value before block
    //     end). Uses in the split block's rename region are renamed up to — and at
    //     a tied redef, including the redef's own read of `value` — the first redef.
    //   * A successor block is "reached carrying V'" if some predecessor carried it
    //     out. Such a block renames its uses of `value` from block entry up to its
    //     first redef of `value`, and carries V' out iff it has no redef of `value`.
    //   * Propagation stops at any block that redefines `value` (it does not carry
    //     out) and never enters a block not reached carrying V'.
    //
    // Deterministic: blocks visited in ascending function-order index; instructions
    // in program order; operands left-to-right.
    // -------------------------------------------------------------------------
    private List<(MirInstruction Instr, int Index)> CollectForwardReachableUses(
        int value, MirBlock atBlock, int atIndex, InsertSide side)
    {
        var sites = new List<(MirInstruction, int)>();

        // A stable index per block for deterministic worklist ordering.
        var blockOrder = new Dictionary<MirBlock, int>(function.Blocks.Count);
        for (var i = 0; i < function.Blocks.Count; i++)
            blockOrder[function.Blocks[i]] = i;

        // Rename `value`'s use operands in `block` over [startIndex, end), in order,
        // stopping at (and INCLUDING the read of) the first instruction that
        // re-defines `value`. Returns true iff the value reaches the block's end
        // with no redef (i.e. the block carries V' out).
        bool RenameRegion(MirBlock block, int startIndex)
        {
            for (var i = startIndex; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                var redefines = DefinesVreg(instr, value);

                // Rename the instruction's USE operands of `value` first — a use is
                // read before this instruction's def point, so even a tied redef's
                // own read takes the relocated (pre-redef) value.
                var operands = instr.Operands;
                for (var k = 0; k < operands.Length; k++)
                    if (operands[k] is VirtualReg u && !u.IsDefinition && u.Id == value)
                        sites.Add((instr, k));

                // A redef ends the relocated value's live region here; downstream
                // (this block's tail + successors) reads the fresh value.
                if (redefines) return false;
            }
            return true; // reached block end with the relocated value still live.
        }

        // Walk the split block's rename region. side=Before begins at `at`; side=
        // After begins strictly after `at`. (atIndex is `at`'s position in the
        // PRE-insertion list, which is the list this method reads.)
        var splitRegionStart = side == InsertSide.Before ? atIndex : atIndex + 1;
        var splitCarriesOut = RenameRegion(atBlock, splitRegionStart);

        if (splitCarriesOut)
        {
            // Forward worklist over successor blocks reached carrying V'. A block B
            // is renamed only when BOTH hold:
            //   (a) the split point DOMINATES B — i.e. `atBlock` dominates B (the
            //       split point sits before atBlock's terminator, so every path
            //       leaving atBlock crosses the split region first). Domination is
            //       what excludes a diamond JOIN (reached from the split arm but
            //       ALSO from the sibling arm, so it reads a value not from the
            //       split) and a loop back-edge re-entry of `atBlock` itself (its
            //       pre-split uses read the original value flowing round the loop,
            //       which legitimately stays live). Pure forward reachability would
            //       wrongly include both.
            //   (b) B is reached carrying V' with no intervening redef (the carry
            //       worklist, stopping at any block that redefines `value`).
            // Together these give exactly the dominance-and-no-redef set: the uses
            // whose ONLY reaching definition is the relocated value.
            var dom = ComputeDominators();

            var reached = new HashSet<MirBlock>();
            var worklist = new List<MirBlock>();
            void Enqueue(MirBlock b)
            {
                // Never re-enter the split block: its pre-split region reads the
                // original value (e.g. round a loop back-edge) and must NOT be
                // renamed. Only enter blocks the split point dominates.
                if (ReferenceEquals(b, atBlock)) return;
                if (!Dominates(dom, atBlock, b)) return;
                if (reached.Add(b)) worklist.Add(b);
            }
            foreach (var succ in OrderedSuccessors(atBlock, blockOrder))
                Enqueue(succ);

            while (worklist.Count > 0)
            {
                // Pop the lowest-ordered block for determinism.
                var idx = 0;
                for (var j = 1; j < worklist.Count; j++)
                    if (blockOrder[worklist[j]] < blockOrder[worklist[idx]]) idx = j;
                var block = worklist[idx];
                worklist.RemoveAt(idx);

                var carriesOut = RenameRegion(block, 0);
                if (carriesOut)
                    foreach (var succ in OrderedSuccessors(block, blockOrder))
                        Enqueue(succ);
            }
        }

        return sites;
    }

    private static List<MirBlock> OrderedSuccessors(
        MirBlock block, Dictionary<MirBlock, int> blockOrder) =>
        block.Successors.OrderBy(s => blockOrder.TryGetValue(s, out var o) ? o : int.MaxValue)
            .ToList();

    // Iterative dataflow dominator sets over the rebuilt CFG (Dom[B] = {B} ∪
    // ⋂_{p∈preds} Dom[p]; entry's Dom is {entry}). Small functions, so the simple
    // O(N²·E) fixpoint is fine — no need for Cooper-Harvey-Kennedy idom. The CFG is
    // already current (LiveIntervalsAnalysis.Compute called RebuildCfg before the
    // intervals this primitive consumes were built). Deterministic: blocks iterated
    // in function order.
    private Dictionary<MirBlock, HashSet<MirBlock>> ComputeDominators()
    {
        var blocks = function.Blocks;
        var dom = new Dictionary<MirBlock, HashSet<MirBlock>>(blocks.Count);

        if (blocks.Count == 0) return dom;
        var entry = blocks[0];

        // Initialize: entry dominated by itself; every other block tentatively
        // dominated by ALL blocks (narrowed by intersection below).
        var all = new HashSet<MirBlock>(blocks);
        foreach (var b in blocks)
            dom[b] = ReferenceEquals(b, entry) ? [entry] : new HashSet<MirBlock>(all);

        bool changed;
        do
        {
            changed = false;
            foreach (var b in blocks)
            {
                if (ReferenceEquals(b, entry)) continue;

                HashSet<MirBlock>? newDom = null;
                foreach (var p in b.Predecessors)
                {
                    if (newDom == null) newDom = new HashSet<MirBlock>(dom[p]);
                    else newDom.IntersectWith(dom[p]);
                }
                newDom ??= []; // an unreachable block with no preds.
                newDom.Add(b);

                if (!newDom.SetEquals(dom[b]))
                {
                    dom[b] = newDom;
                    changed = true;
                }
            }
        }
        while (changed);

        return dom;
    }

    // True iff `a` dominates `b` per the computed dominator sets.
    private static bool Dominates(
        Dictionary<MirBlock, HashSet<MirBlock>> dom, MirBlock a, MirBlock b) =>
        dom.TryGetValue(b, out var set) && set.Contains(a);

    // -------------------------------------------------------------------------
    // Instruction split (split-to-register). Returns true if it split `vreg`;
    // the caller then returns control to the pass for reanalysis. Minted temps
    // are recorded in `splitProducts` so they are never themselves re-split (a
    // temp's only use is the constraining one — re-splitting would loop).
    // -------------------------------------------------------------------------
    public bool TryInstructionSplit(int vreg, ISet<int> splitProducts)
    {
        var flexibleId = registerInfo.FlexibleI8ClassId;
        if (flexibleId == 0) return false;

        // A product we already minted: its sole use is the constraining one, so
        // re-splitting would mint another copy in front of the same use forever.
        if (splitProducts.Contains(vreg)) return false;

        // The def must be a copy. A value defined by a constraining op stays
        // pinned to that op's class no matter how its uses are rewritten, so
        // splitting the uses cannot free it.
        var def = function.GetDefinition(vreg);
        if (def == null || !IsCopyInstr(def)) return false;

        var flexRegs = registerInfo.GetAllocatableRegisters(flexibleId).ToArray();
        var narrowingUses = CollectNarrowingUses(vreg, flexibleId, flexRegs);
        if (narrowingUses.Count == 0) return false; // nothing to relax.

        var flexName = registerInfo.GetRegisterClassName(flexibleId) ?? $"class{flexibleId}";
        var builder = new MirBuilder(function);
        foreach (var (instr, index) in narrowingUses)
        {
            var temp = function.CreateVirtualRegisterInClass(flexibleId, flexName);
            splitProducts.Add(temp);
            builder.SetInsertionPointBefore(instr);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(temp, IsDefinition: true),
                new VirtualReg(vreg, IsDefinition: false));
            instr.Operands[index] = new VirtualReg(temp, IsDefinition: false);
        }
        return true;
    }

    // Narrowing non-copy uses of `vreg`: an operand whose declared class is a
    // strict subset of the flexible class. (Copies impose no class and are
    // skipped — they are exactly the plumbing the split relies on.)
    private List<(MirInstruction Instr, int Index)> CollectNarrowingUses(
        int vreg, int flexibleId, int[] flexRegs)
    {
        var uses = new List<(MirInstruction, int)>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (IsCopyInstr(instr)) continue;
                var info = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code);
                var classes = info.OperandClasses;
                if (classes == null) continue;

                var operands = instr.Operands;
                for (var k = 0; k < operands.Length && k < classes.Length; k++)
                {
                    if (operands[k] is not VirtualReg u || u.IsDefinition || u.Id != vreg)
                        continue;
                    // Skip a TIED use: routing it through a copy buys nothing —
                    // the tie re-pins the fresh temp to the def's class, so the
                    // constraint is unchanged (the temp inherits {$a} just as the
                    // original did). The tied-accumulator copy of a two-address
                    // adc/sbc is exactly this case; "splitting" it only renames the
                    // value and grows its range (the Stage-R2 measure showed it
                    // making no progress). The relocation kinds handle this shape.
                    if (info.GetTiedToIndex(k) >= 0) continue;
                    var opClass = classes[k];
                    if (opClass == 0 || opClass == flexibleId) continue;
                    var opRegs = registerInfo.GetAllocatableRegisters(opClass);
                    if (IsStrictSubset(opRegs, flexRegs))
                        uses.Add((instr, k));
                }
            }
        return uses;
    }

    // -------------------------------------------------------------------------
    // -------------------------------------------------------------------------
    // Local split (gap-based, within one block). Reference: RegAllocGreedy.cpp
    // tryLocalSplit / calcGapWeights.
    //
    // The case this exists for: a value whose WHOLE range fits no single register
    // (so tryAssign already failed), but which CAN be coloured if cut into pieces
    // because its halves can each take a DIFFERENT free register — reg R1 free in
    // the first half but busy in the second, R2 the reverse. We pick a split
    // point (an interior use boundary) such that some register is free across the
    // first piece and some register is free across the second piece, then cut the
    // range there: mint a fresh temp in the value's class, copy the value into it
    // right after the split-point use, and rewrite the later uses to the temp. The
    // next reanalysis colours the two shorter pieces independently (each finds its
    // own free register), and the boundary copy lowers to a cheap register move.
    //
    // The "is register R free across [start, end)?" test is supplied by the caller
    // (`isBusyAcross`) because only the allocator knows what it has COMMITTED to
    // each register this round (its LiveRegMatrix analogue) plus the precoloured
    // physreg windows — the SplitEditor cannot derive that from LiveIntervals
    // alone. We split at the FIRST viable interior use boundary rather than
    // computing true BlockFrequency gap weights (LLVM's full calcGapWeights cost
    // model); that captures the right shape — cut where each piece can be coloured
    // — without the weight model. Refine if a corpus case demands it.
    //
    // Termination: each split strictly reduces the per-range use count (the two
    // pieces each have fewer uses than the original), so repeated local splitting
    // bottoms out at ranges with too few uses to split — which then assign or
    // spill. Returns true if it split.
    // -------------------------------------------------------------------------
    public bool TryLocalSplit(
        int vreg, LiveIntervals intervals, int[] allowed,
        Func<int, int, int, bool> isBusyAcross)
    {
        var def = function.GetDefinition(vreg);
        if (def == null) return false; // a livein with no def cannot be locally split.

        // Splitting only helps a value defined and used within ONE block — a
        // cross-block range needs region splitting (Stage 5, out of scope).
        var block = def.Parent!;
        var useIndices = UseIndicesInBlock(block, vreg);
        if (useIndices.Count < 2) return false; // need an interior use boundary.

        var interval = intervals.IntervalOf(vreg);
        if (interval.IsEmpty) return false;
        var lo = interval.Start;
        var hi = interval.End;

        // Guard against a pointless split: if some register is free across the
        // whole range, tryAssign should have taken it, so cutting buys nothing.
        if (AnyFree(allowed, lo, hi, isBusyAcross)) return false;

        // Try each interior use boundary as the split point. The split point is
        // the slot just AFTER use[g]'s read (its def sub-slot): the first piece is
        // [lo, split), the second piece [split, hi). Cut at the first boundary
        // where each piece independently has a free register.
        for (var g = 0; g < useIndices.Count - 1; g++)
        {
            var split = LiveIntervals.DefSlot(intervals.BaseSlotOf[block.Instructions[useIndices[g]]]);
            if (split <= lo || split >= hi) continue;

            if (!AnyFree(allowed, lo, split, isBusyAcross)) continue;
            if (!AnyFree(allowed, split, hi, isBusyAcross)) continue;

            SplitAfter(vreg, block, useIndices[g], useIndices[g + 1]);
            return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Split around a call (the live-across-call lever). Reference:
    // RegAllocGreedy.cpp tryRegionSplit — but the single contiguous across-call
    // region here is a targeted RELOCATE, not a full edge-bundle region split.
    //
    // The case this exists for: a value live ACROSS a call whose register class
    // is entirely CALLER-SAVED (e.g. `axy` = {$a,$x,$y}, all clobbered by a
    // call). A call instruction carries the caller-saved registers as explicit
    // implicit-DEFs (the clobber barrier emitted by CallLowering), so they appear
    // in PhysRegIntervals as precoloured busy windows covering the call's
    // def-point. A value live across that point therefore overlaps every one of
    // its allowed (caller-saved) registers there: tryAssign fails (no whole-range
    // free reg), tryEvict fails (the clobbers are fixed precoloured windows, not
    // evictable committed vregs), instruction-split bails (the def is not a
    // narrowing copy) and local-split bails (no register in the value's class is
    // free across the across-call sub-region — they are all clobbered, and the
    // class has no callee-saved member to escape to).
    //
    // The fix mirrors llvm-mos exactly. llvm-mos relocates `a` to a callee-saved
    // __rcNN ACROSS the call and copies it BACK to a GPR for the post-call use
    // (`sta __rc20` before the call, `lda __rc20` / `tax` after). We do the same:
    //   * mint a relocation temp in the FLEXIBLE class (which DOES include the
    //     callee-saved zero-page registers);
    //   * copy the value into the temp right BEFORE the call (`temp = copy v`);
    //   * RE-DEFINE the value from the temp right AFTER the call (`v = copy temp`),
    //     so the value's live range is cut into two GPR-friendly pieces (before /
    //     after the call) with the temp carrying it across.
    // The next reanalysis sees the temp live across the call, so it overlaps every
    // caller-saved clobber window and the ONLY registers free for it are the
    // callee-saved ones — tryAssign homes it there automatically (no dedicated
    // callee-saved class needed; the clobber windows do the constraining, exactly
    // as llvm-mos inflates to the largest legal superclass and lets interference
    // pick). The value's two short pieces each take a caller-saved register (its
    // class is unchanged, so the constrained post-call use is satisfied), and
    // PrologueEpilogueInsertionPass emits the save/restore for whichever
    // callee-saved register the temp landed on.
    //
    // The "is register R free across [start, end)?" busy test is supplied by the
    // caller (the allocator's committed-assignment + precoloured-window picture);
    // we use it to confirm a callee-saved register IS actually free across the
    // region before splitting, so we never relocate into an occupied reg.
    //
    // Single contiguous across-call region only — no edge bundles, no global
    // region split (out of scope; add only if a corpus case demands it).
    // Returns true if it split.
    // -------------------------------------------------------------------------
    public bool TrySplitAroundCall(
        int vreg, LiveIntervals intervals, int[] allowed,
        int[] calleeSavedRegs, ISet<int> splitProducts,
        Func<int, int, int, bool> isBusyAcross)
    {
        if (calleeSavedRegs.Length == 0) return false;

        // A relocation temp we already minted: its sole role is carrying the value
        // across the call, so re-splitting it would loop.
        if (splitProducts.Contains(vreg)) return false;
        var interval = intervals.IntervalOf(vreg);
        if (interval.IsEmpty) return false;

        // Find the clobber barrier this value lives across: a call instruction
        // (one carrying implicit-def physregs) such that the value is live both
        // BEFORE and AFTER its def-point, and the clobbers cover the value's
        // allowed registers (so the value genuinely cannot stay put across it).
        var (call, callBlock, callIndex) = FindAcrossCallBarrier(vreg, intervals, allowed);
        if (call == null) return false;

        // The across-call region runs from the call to the value's last use.
        var callDefSlot = LiveIntervals.DefSlot(intervals.BaseSlotOf[call]);
        var hi = interval.End;
        if (callDefSlot >= hi) return false; // no live range after the call.

        // Confirm a callee-saved register is actually free across [call, hi). If
        // none is, relocation buys nothing (and the temp would itself fail to
        // allocate), so leave the value for the spill fallback.
        if (!AnyFree(calleeSavedRegs, callDefSlot, hi, isBusyAcross))
            return false;

        // Mint the relocation temp in the FLEXIBLE class so its across-call
        // interference with the caller-saved clobber windows leaves only the
        // callee-saved registers free for it.
        var flexibleId = registerInfo.FlexibleI8ClassId;
        if (flexibleId == 0) return false;
        var flexName = registerInfo.GetRegisterClassName(flexibleId) ?? $"class{flexibleId}";
        var temp = function.CreateVirtualRegisterInClass(flexibleId, flexName);
        splitProducts.Add(temp);

        // Copy the value into the temp right BEFORE the call (`temp = copy v`),
        // and re-define the value from the temp right AFTER the call
        // (`v = copy temp`). The value's range is cut into two pieces (before /
        // after the call, each free to take a caller-saved register), and the temp
        // carries it across the call (forced to a callee-saved register because
        // every caller-saved one is clobbered there).
        callBlock!.InsertInstruction(
            callIndex,
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(temp, IsDefinition: true),
            new VirtualReg(vreg, IsDefinition: false));

        // The pre-call copy shifted the call (and everything after) down by one;
        // the call is now at callIndex + 1, so the copy-back goes at callIndex + 2.
        callBlock.InsertInstruction(
            callIndex + 2,
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(vreg, IsDefinition: true),
            new VirtualReg(temp, IsDefinition: false));
        return true;
    }

    // -------------------------------------------------------------------------
    // Split-around-clobber (constrained-def-result relocation). Reference:
    // RegAllocGreedy.cpp tryInstructionSplit (1587) + the register-class
    // inflation machinery (getLargestLegalSuperClass): when a value is produced
    // in a SINGLE-physreg class and a later instruction re-defines that same
    // physreg while the value is still live, the value must be relocated off the
    // constrained register into a wider (flexible) class so it can live elsewhere
    // (zero page) across the re-clobber. The two single-physreg values cannot
    // share the one register, so ONE of them must vacate it.
    //
    // The case this exists for (the Stage-4 blocker): after isel an adc/sbc/load
    // result is pinned to class `ac` (the single physreg $a). In an i16/i32 add
    // chain the next adc's result is ALSO `ac` — so it re-defines $a while the
    // first result is still live (it is read later, after the second adc):
    //
    //   %19 : ac = mos6502.adc ...      ; result in $a
    //   %21 : ac = mos6502.adc ...      ; ALSO defines $a — re-clobbers it
    //   $a = pseudo.copy %19            ; %19 still needed here
    //
    // %19 is live across %21's $a def, so it must vacate $a before %21. assign
    // fails (its only allowed reg, $a, is busy across the re-clobber), evict fails
    // (the re-clobber is a constrained def, not an evictable committed vreg of
    // lighter weight), instruction-split bails (the def is a non-copy adc), local-
    // split bails (the conflict is a constrained re-def, not a free-register gap),
    // and split-around-call bails (no implicit-def clobber barrier). So none of
    // the other kinds fire — this kind is the one that does.
    //
    // The fix mirrors InsertRelocationCopiesForConstrainedDefs (the colourer's
    // eager pre-pass) ON-DEMAND: mint a FLEXIBLE-class temp right AFTER `vreg`'s
    // defining instruction, copy the value into it (`temp = copy vreg`), and
    // redirect `vreg`'s later in-block uses to `temp`. The next reanalysis sees
    // `vreg` occupy $a only for the brief def→copy window (no longer live across
    // the re-clobber), and the flexible temp carrying the value lives in the
    // abundant zero-page file — exactly the relocation the deleted isel out-funnel
    // performed. The temp is recorded in `splitProducts` so it is never itself
    // re-split.
    //
    // Trigger (precise): `vreg`'s allowed set is a single physreg R (so it is
    // pinned to one register), and there is a LATER instruction — past `vreg`'s
    // def, while `vreg` is still live — that DEFINES some other vreg whose own
    // allowed set is also exactly {R} (the re-clobber). We frame it on the
    // post-intersection allowed sets (`allowedByVreg`) rather than the class
    // because that is what RA actually colours against: a value whose allowed set
    // is {R} genuinely cannot live anywhere but R, and a later def of another
    // {R}-pinned value is the precise re-clobber that forces relocation.
    // Returns true if it split.
    // -------------------------------------------------------------------------
    public bool TrySplitConstrainedDefResult(
        int vreg, LiveIntervals intervals,
        IReadOnlyDictionary<int, int[]> allowedByVreg, ISet<int> splitProducts)
    {
        var flexibleId = registerInfo.FlexibleI8ClassId;
        if (flexibleId == 0) return false;

        // A relocation temp we already minted: its def is the copy itself and its
        // sole use is the constraining one, so re-splitting it would loop.
        if (splitProducts.Contains(vreg)) return false;

        // `vreg` must be pinned to a single physreg R.
        if (!allowedByVreg.TryGetValue(vreg, out var myAllowed) || myAllowed.Length != 1)
            return false;
        var pinnedReg = myAllowed[0];

        // R must be a member of the flexible class — the relocation parks the value
        // in a flexible-class temp, so the value must be able to live there. This
        // is what restricts the kind to data registers ($a / $x / $y, all in the
        // flexible class) and excludes flag registers ($c / $n / $z / $v), which a
        // pseudo.copy can never carry. (The colourer's eager equivalent only ever
        // fires on tied adc/sbc def operands, never on flag defs, for the same
        // reason.)
        if (!RegisterAllocationSupport.Contains(
                registerInfo.GetAllocatableRegisters(flexibleId), pinnedReg))
            return false;

        // `vreg` must have a real def (a livein has no def to relocate after).
        var def = function.GetDefinition(vreg);
        if (def == null) return false;

        var interval = intervals.IntervalOf(vreg);
        if (interval.IsEmpty) return false;

        // Find a later instruction that re-defines R (defines some OTHER vreg also
        // pinned to {R}) while `vreg` is live across it. The re-clobber's def-point
        // must fall strictly inside `vreg`'s live range (covered, and before its
        // end) — that is exactly "live across the re-clobber".
        var reClobber = FindLaterReClobber(vreg, pinnedReg, def, intervals, interval, allowedByVreg);
        if (reClobber == null) return false;

        var clobberPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[reClobber]);
        return RelocateAcrossClobber(vreg, clobberPoint, intervals, splitProducts);
    }

    // -------------------------------------------------------------------------
    // Split-not-spill: relocate a single-physreg-pinned value off a PRECOLOURED
    // physreg clobber it lives across. Reference: llvm-mos RegAllocGreedy.cpp
    // RS_LightSpill — "spill to a wider register class to avoid spilling to the
    // stack" — driven by tryInstructionSplit with the inflate-to-superclass flag.
    //
    // The case this exists for (the Stage-R2 chain blocker, distinct from
    // TrySplitConstrainedDefResult): a value `vreg` pinned to a single physreg R
    // (e.g. the HIGH adc result `%21:any8` allowed={$a} in an i16 chain) is live
    // ACROSS a later DIRECT physreg def of R — not another {R}-pinned vreg, but a
    // precoloured `$a = pseudo.copy …` (the ABI return move that parks the LOW
    // byte into $a). LowerReturn emits the per-byte copies LSB-first, so the low
    // byte's `$a = copy …` is scheduled BEFORE the high byte's `$x = copy %21`,
    // and that `$a` write clobbers $a while `%21` is still needed for its own
    // return move. The colourer never hits this because the isel out-funnel
    // relocates BOTH adc results off $a before RA; greedy (funnels off) must
    // reconstruct that relocation here, on demand.
    //
    // FindLaterReClobber does NOT catch this: the re-clobber is a PHYSREG def of R
    // (operand `$a`, IsDefinition), not a vreg whose allowed set is {R}. So we
    // detect the clobber directly off R's precoloured busy interval: the earliest
    // point strictly after `vreg`'s producing def where R is precoloured-busy and
    // `vreg` is still live. Relocating `vreg`'s across-clobber use(s) to a flexible
    // temp (RelocateAcrossClobber) frees R for the precoloured write; `vreg` then
    // occupies R only for the brief def→copy window. This is the relocatable case:
    // R is a flexible-class member, so the value CAN live elsewhere — split is
    // strictly better than the memory spill the value would otherwise hit.
    // Returns true if it relocated.
    // -------------------------------------------------------------------------
    public bool TrySplitRelocatableAcrossPhysClobber(
        int vreg, LiveIntervals intervals,
        IReadOnlyDictionary<int, int[]> allowedByVreg, ISet<int> splitProducts)
    {
        var flexibleId = registerInfo.FlexibleI8ClassId;
        if (flexibleId == 0) return false;
        if (splitProducts.Contains(vreg)) return false;

        // `vreg` must be pinned to a single physreg R that is a flexible-class
        // member (so the relocation temp can carry the value off R — the same
        // data-register / no-flag restriction as TrySplitConstrainedDefResult).
        if (!allowedByVreg.TryGetValue(vreg, out var myAllowed) || myAllowed.Length != 1)
            return false;
        var pinnedReg = myAllowed[0];
        if (!RegisterAllocationSupport.Contains(
                registerInfo.GetAllocatableRegisters(flexibleId), pinnedReg))
            return false;

        var def = function.GetDefinition(vreg);
        if (def == null) return false;
        var interval = intervals.IntervalOf(vreg);
        if (interval.IsEmpty) return false;

        var defPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[def]);

        // The earliest precoloured-R def-point strictly after `vreg`'s def that
        // `vreg` is live across: a point covered by both `vreg`'s interval and R's
        // precoloured busy interval, lying strictly inside `vreg`'s range. A
        // precoloured def opens R's segment at its def-point, so the segment START
        // that is > defPoint and < interval.End and covered by `vreg` is the
        // clobber `vreg` must vacate R before.
        var physInterval = intervals.PhysIntervalOf(pinnedReg);
        if (physInterval.IsEmpty) return false;

        long clobberPoint = long.MaxValue;
        foreach (var seg in physInterval.Segments)
        {
            if (seg.Start <= defPoint) continue;       // not later than our def.
            if (seg.Start >= interval.End) continue;   // we die at/before it.
            if (!interval.Covers(seg.Start)) continue; // we are not live across it.
            if (seg.Start < clobberPoint) clobberPoint = seg.Start;
        }
        if (clobberPoint == long.MaxValue) return false;

        return RelocateAcrossClobber(vreg, clobberPoint, intervals, splitProducts);
    }

    // -------------------------------------------------------------------------
    // Split-around-clobber, INCUMBENT direction. Companion to
    // TrySplitConstrainedDefResult for the common carry-chain shape where the
    // FAILING value is the re-clobbering def, not the value live across it:
    //   %19 = adc ...        ; low result, pinned {$a}, live across the next adc
    //   %21 = adc ...        ; high result, ALSO pinned {$a}  ← the re-clobber
    //   $a  = copy %19       ; %19 still needed
    // The allocator assigns $a to the longer %19 first (largest-interval-first), so
    // %21 fails. %21 cannot split itself (not live across a later {$a} def) and
    // cannot evict the heavier %19 — so without this it spills uselessly forever.
    // The value that must move is the incumbent %19. Given the failing re-clobbering
    // value, find the {R}-pinned value live across its def IN THE SAME BLOCK (the
    // straight-line region the block-local relocation can shorten) and relocate THAT
    // — the same edit as TrySplitConstrainedDefResult. The next reanalysis shortens
    // %19 to a brief def→copy window, %21 takes $a, and the relocated temp lives in
    // the flexible file. (llvm-mos reaches the same outcome via
    // evict→requeue→split-at-higher-stage; Irie's reanalysis architecture splits the
    // incumbent directly.) Returns true if it split.
    // -------------------------------------------------------------------------
    public bool TrySplitIncumbentAcrossClobber(
        int clobberingVreg, LiveIntervals intervals,
        IReadOnlyDictionary<int, int[]> allowedByVreg, ISet<int> splitProducts)
    {
        var flexibleId = registerInfo.FlexibleI8ClassId;
        if (flexibleId == 0) return false;

        // The failing value must itself be pinned to a single physreg R and define
        // it (its def is the re-clobber point that conflicts with the incumbent).
        if (!allowedByVreg.TryGetValue(clobberingVreg, out var mine) || mine.Length != 1)
            return false;
        var pinnedReg = mine[0];
        if (!RegisterAllocationSupport.Contains(
                registerInfo.GetAllocatableRegisters(flexibleId), pinnedReg))
            return false;
        var clobberDef = function.GetDefinition(clobberingVreg);
        if (clobberDef == null) return false;
        var clobberBlock = clobberDef.Parent!;
        var clobberPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[clobberDef]);

        // Find an incumbent pinned {R}, live across the clobber, defined in the same
        // block. Iterate in ascending vreg id for determinism; relocate the first.
        foreach (var v1 in function.VirtualRegisterIds.OrderBy(x => x))
        {
            if (v1 == clobberingVreg || splitProducts.Contains(v1)) continue;
            if (!allowedByVreg.TryGetValue(v1, out var a) || a.Length != 1 || a[0] != pinnedReg)
                continue;
            var iv = intervals.IntervalOf(v1);
            if (iv.IsEmpty) continue;
            var v1def = function.GetDefinition(v1);
            if (v1def == null || !ReferenceEquals(v1def.Parent, clobberBlock)) continue;
            var v1defPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[v1def]);
            if (v1defPoint >= clobberPoint) continue;   // defined after the clobber.
            if (!iv.Covers(clobberPoint)) continue;     // not live across the clobber.
            if (clobberPoint >= iv.End) continue;       // dies at/before the clobber.
            return RelocateAcrossClobber(v1, clobberPoint, intervals, splitProducts);
        }
        return false;
    }

    // The relocation edit shared by both split-around-clobber directions: mint a
    // flexible-class temp right AFTER `vreg`'s producing def, copy the value into
    // it (`temp = copy vreg`), and redirect EVERY use of that value — across ALL
    // blocks — to the temp. `vreg` then occupies its pinned register only for the
    // brief def→copy window; the flexible temp carries the value across the
    // re-clobber in the abundant zero-page / GPR file — the on-demand mirror of
    // InsertRelocationCopiesForConstrainedDefs / the deleted isel out-funnel. The
    // temp is recorded in `splitProducts` so it is never itself re-split.
    //
    // Slot-precise + cross-block (Stage R1). The redirect is keyed off the global
    // SlotIndex numbering, not the defining block: a use of `vreg`'s value is any
    // use whose use-slot lies strictly after the producing def's def-slot and
    // strictly before `vreg`'s NEXT def (so a later, unrelated re-definition's
    // value is untouched). Because LiveIntervals numbers slots in program order
    // across every block, this naturally redirects the cross-block uses that the
    // old block-local loop left behind (the Stage-R0 Mode-B non-termination: the
    // donor range never shrank because its cross-block uses kept it live across the
    // re-clobbers). Reanalysis between rounds then reconstructs liveness precisely,
    // and the donor range strictly shrinks because the across-clobber uses have
    // moved to the temp — asserted below so a non-shrinking edit fails loudly
    // rather than looping to the round cap.
    private bool RelocateAcrossClobber(
        int vreg, long clobberPoint, LiveIntervals intervals, ISet<int> splitProducts)
    {
        // In TWO-ADDRESS (non-SSA) form `vreg` may have several defs — e.g. a tied-
        // accumulator copy AND the adc that overwrites it (`%19 = copy %3` then
        // `%19 = adc %19, …`). The value live ACROSS the clobber is the one produced
        // by `vreg`'s LATEST def before the clobber point, so we relocate after THAT
        // def — NOT GetDefinition's first def, which would copy the adc INPUT
        // (`arg0lo`) instead of its RESULT. (Mirrors llvm-mos targeting the precise
        // def SlotIndex; the prior first-def relocation is what made carry chains
        // spill instead of split.)
        MirBlock? defBlock = null;
        var defIndex = -1;
        var producingDefSlot = long.MinValue;
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (!DefinesVreg(instr, vreg)) continue;
                var slot = LiveIntervals.DefSlot(intervals.BaseSlotOf[instr]);
                if (slot < clobberPoint && slot > producingDefSlot)
                {
                    producingDefSlot = slot; defBlock = block; defIndex = i;
                }
            }
        if (defBlock == null) return false;

        // S3: route the relocation through the value-number-precise split primitive.
        // SplitAtPoint mints the flexible-class temp, records it in splitProducts,
        // inserts the boundary copy AFTER the producing def, and renames exactly the
        // downstream uses reached forward from the split point via dominance +
        // stop-at-redef over the CFG — replacing the old program-order slot-window
        // redirect (which mis-renamed sibling/back-edge uses on branching code). It
        // returns false when nothing downstream relocates; propagate that.
        var producingDefInstruction = defBlock.Instructions[defIndex];
        return SplitAtPoint(vreg, producingDefInstruction, InsertSide.After, intervals, splitProducts);
    }

    private static bool DefinesVreg(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (op is VirtualReg d && d.IsDefinition && d.Id == vreg) return true;
        return false;
    }

    // Find the later instruction that re-defines `vreg`'s pinned register while
    // `vreg` is still live across it (or null). "Re-defines" = defines some OTHER
    // vreg whose allowed set is also exactly {pinnedReg}; "live across" = the
    // def-point of that instruction is covered by `vreg`'s interval and lies
    // strictly before `vreg`'s end (so the value outlives the re-clobber and
    // genuinely conflicts). Returns the EARLIEST such re-clobber — that is the one
    // `vreg`'s value must vacate the register before.
    private MirInstruction? FindLaterReClobber(
        int vreg, int pinnedReg, MirInstruction def, LiveIntervals intervals,
        LiveInterval interval, IReadOnlyDictionary<int, int[]> allowedByVreg)
    {
        var defPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[def]);
        MirInstruction? best = null;
        var bestSlot = long.MaxValue;
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (ReferenceEquals(instr, def)) continue;
                var reDefPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[instr]);
                if (reDefPoint <= defPoint) continue;          // not later.
                if (reDefPoint >= bestSlot) continue;          // a later re-clobber.
                if (!interval.Covers(reDefPoint)) continue;    // `vreg` not live across it.
                if (reDefPoint >= interval.End) continue;      // dies at/before the re-def.

                // Does this instruction define another {pinnedReg}-pinned vreg?
                foreach (var op in instr.Operands)
                {
                    if (op is not VirtualReg d || !d.IsDefinition || d.Id == vreg) continue;
                    if (allowedByVreg.TryGetValue(d.Id, out var a)
                        && a.Length == 1 && a[0] == pinnedReg)
                    {
                        best = instr; bestSlot = reDefPoint; break;
                    }
                }
            }
        return best;
    }

    // Find a call barrier the value lives across. A "call" here is recognised
    // structurally: an instruction with one or more implicit-DEF physreg operands
    // (the clobber barrier CallLowering attaches to a jsr). We require the value
    // to be live across the instruction's def-point (used after it AND live into
    // it) and the clobbers to cover the value's allowed register set, so the
    // value genuinely cannot remain in any allowed register across the call.
    // Returns the first such instruction (ascending program order) or null.
    private (MirInstruction? Call, MirBlock? Block, int Index) FindAcrossCallBarrier(
        int vreg, LiveIntervals intervals, int[] allowed)
    {
        var interval = intervals.IntervalOf(vreg);
        foreach (var block in function.Blocks)
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                var clobbers = ImplicitClobbers(instr);
                if (clobbers.Count == 0) continue;

                // The value must be live across this instruction's def-point: live
                // before it (covers the use point) AND used somewhere after it.
                var usePoint = LiveIntervals.UseSlot(intervals.BaseSlotOf[instr]);
                var defPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[instr]);
                if (!interval.Covers(usePoint)) continue;
                if (interval.End <= defPoint) continue; // dies at/before the call.

                // The value must not appear as an operand of the call itself (an
                // arg / clobber-target) — we only relocate values that merely pass
                // OVER the call, not ones it reads or writes.
                if (UsesOrDefsVreg(instr, vreg)) continue;

                // Every allowed register must be clobbered by the call, so the
                // value cannot stay in any of them across it.
                if (AllClobbered(allowed, clobbers))
                    return (instr, block, i);
            }
        return (null, null, 0);
    }

    private static HashSet<int> ImplicitClobbers(MirInstruction instr)
    {
        var set = new HashSet<int>();
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: true, IsImplicit: true } p)
                set.Add(p.Id);
        return set;
    }

    private static bool AllClobbered(int[] allowed, HashSet<int> clobbers)
    {
        foreach (var r in allowed)
            if (!clobbers.Contains(r)) return false;
        return allowed.Length > 0;
    }

    private static bool UsesOrDefsVreg(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (op is VirtualReg v && v.Id == vreg) return true;
        return false;
    }

    // Is some register in `allowed` free across the whole half-open window
    // [start, end) — i.e. not busy anywhere within it?
    private static bool AnyFree(
        int[] allowed, int start, int end, Func<int, int, int, bool> isBusyAcross)
    {
        foreach (var reg in allowed)
            if (!isBusyAcross(reg, start, end))
                return true;
        return false;
    }

    // The instruction indices in `block` at which `vreg` is used (read).
    private static List<int> UseIndicesInBlock(MirBlock block, int vreg)
    {
        var indices = new List<int>();
        for (var i = 0; i < block.Instructions.Count; i++)
            foreach (var op in block.Instructions[i].Operands)
                if (op is VirtualReg v && !v.IsDefinition && v.Id == vreg)
                {
                    indices.Add(i);
                    break;
                }
        return indices;
    }

    // Cut `vreg` after the use at block index `afterIdx`: mint a fresh temp in
    // `vreg`'s class, copy `vreg` → temp right after that use, and rewrite every
    // use from `beforeIdx` (the next use) onward in this block to the temp. The
    // next reanalysis colours the two pieces independently.
    private void SplitAfter(int vreg, MirBlock block, int afterIdx, int beforeIdx)
    {
        var (classId, className) = ClassOf(vreg);
        var temp = function.CreateVirtualRegisterInClass(classId, className);

        block.InsertInstruction(
            afterIdx + 1,
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(temp, IsDefinition: true),
            new VirtualReg(vreg, IsDefinition: false));

        // The insert shifted everything at/after afterIdx+1 by one.
        for (var i = beforeIdx + 1; i < block.Instructions.Count; i++)
        {
            var operands = block.Instructions[i].Operands;
            for (var k = 0; k < operands.Length; k++)
                if (operands[k] is VirtualReg v && !v.IsDefinition && v.Id == vreg)
                    operands[k] = new VirtualReg(temp, IsDefinition: false);
        }
    }

    private (int ClassId, string Name) ClassOf(int vreg) =>
        function.GetVRegAnnotation(vreg) is ClassedVReg c
            ? (c.ClassId, c.Name)
            : throw new InvalidOperationException(
                $"SplitEditor: cannot split vreg %{vreg} — it has no register class.");

    private static bool IsCopyInstr(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;

    // True iff every register in `sub` is in `super` and `super` has more — i.e.
    // `sub` ⊊ `super`, so constraining a flexible value to `sub` genuinely loses
    // registers (and splitting around the use buys the value the wider class).
    private static bool IsStrictSubset(ReadOnlySpan<int> sub, int[] super)
    {
        if (sub.Length >= super.Length) return false;
        foreach (var r in sub)
            if (!RegisterAllocationSupport.Contains(super, r)) return false;
        return true;
    }
}
