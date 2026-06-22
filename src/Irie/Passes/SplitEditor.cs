using Irie.Dialects.Pseudo;
using Irie.Mir;
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
    // Local split (gap-based, within one block) — DEFERRED to Stage 2b. The
    // mechanical boundary-copy primitive is the same shape as the instruction
    // split above (mint a temp, insert a `pseudo.copy`, rewrite the in-region
    // uses), so the editor already has the substrate. What it lacks is the right
    // CONTENTION model: the case local split exists for is a value whose WHOLE
    // range fits no single register, but whose halves can each take a DIFFERENT
    // free register (reg R1 free in the first half / busy in the second, R2 the
    // reverse). The naive "every allowed register is busy across the gap" test is
    // a different, mostly-inert condition (it models a value that can hold NO
    // register across the gap — closer to a hole than a split), so it is omitted
    // rather than shipped wrong. Stage 2b adds the per-subrange free-register
    // check (LLVM `calcGapWeights` / `tryLocalSplit`) that makes this fire on the
    // cases it is meant for (e.g. early-return once the manual funnels are gone).

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
