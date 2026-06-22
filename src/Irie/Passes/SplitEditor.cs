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
