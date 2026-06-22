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
                var classes = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).OperandClasses;
                if (classes == null) continue;

                var operands = instr.Operands;
                for (var k = 0; k < operands.Length && k < classes.Length; k++)
                {
                    if (operands[k] is not VirtualReg u || u.IsDefinition || u.Id != vreg)
                        continue;
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
        if (!HasLaterReClobber(vreg, pinnedReg, def, intervals, interval, allowedByVreg))
            return false;

        return RelocateAcrossClobber(vreg, splitProducts);
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
            return RelocateAcrossClobber(v1, splitProducts);
        }
        return false;
    }

    // The relocation edit shared by both split-around-clobber directions: mint a
    // flexible-class temp right AFTER `vreg`'s def, copy the value into it
    // (`temp = copy vreg`), and redirect `vreg`'s later in-block uses to the temp.
    // `vreg` then occupies its pinned register only for the brief def→copy window;
    // the flexible temp carries the value across the re-clobber in the abundant
    // zero-page / GPR file — the on-demand mirror of
    // InsertRelocationCopiesForConstrainedDefs / the deleted isel out-funnel. The
    // temp is recorded in `splitProducts` so it is never itself re-split. Block-
    // local redirect: the straight-line arithmetic chains this targets keep the
    // value within the defining block. Returns true.
    private bool RelocateAcrossClobber(int vreg, ISet<int> splitProducts)
    {
        var flexibleId = registerInfo.FlexibleI8ClassId;
        var def = function.GetDefinition(vreg);
        if (def == null) return false;
        var defBlock = def.Parent!;
        var defIndex = defBlock.Instructions.IndexOf(def);

        var flexName = registerInfo.GetRegisterClassName(flexibleId) ?? $"class{flexibleId}";
        var temp = function.CreateVirtualRegisterInClass(flexibleId, flexName);
        splitProducts.Add(temp);

        defBlock.InsertInstruction(
            defIndex + 1,
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(temp, IsDefinition: true),
            new VirtualReg(vreg, IsDefinition: false));

        // The insert shifted everything at/after defIndex+1 by one; later uses
        // start at defIndex + 2.
        for (var j = defIndex + 2; j < defBlock.Instructions.Count; j++)
        {
            var operands = defBlock.Instructions[j].Operands;
            for (var k = 0; k < operands.Length; k++)
                if (operands[k] is VirtualReg u && !u.IsDefinition && u.Id == vreg)
                    operands[k] = new VirtualReg(temp, IsDefinition: false);
        }
        return true;
    }

    // Is there a later instruction that re-defines `vreg`'s pinned register while
    // `vreg` is still live across it? "Re-defines" = defines some OTHER vreg whose
    // allowed set is also exactly {pinnedReg}; "live across" = the def-point of
    // that instruction is covered by `vreg`'s interval and lies strictly before
    // `vreg`'s end (so the value outlives the re-clobber and genuinely conflicts).
    private bool HasLaterReClobber(
        int vreg, int pinnedReg, MirInstruction def, LiveIntervals intervals,
        LiveInterval interval, IReadOnlyDictionary<int, int[]> allowedByVreg)
    {
        var defPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[def]);
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (ReferenceEquals(instr, def)) continue;
                var reDefPoint = LiveIntervals.DefSlot(intervals.BaseSlotOf[instr]);
                if (reDefPoint <= defPoint) continue;          // not later.
                if (!interval.Covers(reDefPoint)) continue;    // `vreg` not live across it.
                if (reDefPoint >= interval.End) continue;      // dies at/before the re-def.

                // Does this instruction define another {pinnedReg}-pinned vreg?
                foreach (var op in instr.Operands)
                {
                    if (op is not VirtualReg d || !d.IsDefinition || d.Id == vreg) continue;
                    if (allowedByVreg.TryGetValue(d.Id, out var a)
                        && a.Length == 1 && a[0] == pinnedReg)
                        return true;
                }
            }
        return false;
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
