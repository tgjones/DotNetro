using Irie.Mir;

namespace Irie.Target;

// Target hook for prologue/epilogue (frame) lowering, mirroring LLVM's
// TargetFrameLowering. The generic PrologueEpilogueInsertionPass owns the
// policy — which callee-saved registers a function actually clobbers, where the
// entry block and the return instructions are, the order in which save/restore
// happen — and delegates the actual instruction emission to a target subclass.
//
// Only the save/restore *instruction emission* is target-specific (on the
// MOS6502 the hardware stack `pha`/`pla` pair plus the `$a`-preservation
// `tay`/`tya` dance); the framework that drives it is target-independent. This
// is the FrameLowering counterpart to the existing LegalizerInfo /
// InstructionSelector / PseudoExpander / BranchLowering generic-pass + target-
// hook idiom.
public abstract class FrameLowering
{
    // True iff `instr` is the target's return instruction (e.g. mos6502.rts).
    // The generic pass uses this to find the epilogue insertion points; mirrors
    // LLVM's MachineInstr::isReturn.
    public abstract bool IsReturnInstruction(MirInstruction instr);

    // Emit the prologue save sequence for `saved` (the callee-saved registers
    // the function actually clobbers, supplied in ASCENDING order) at the TOP of
    // `entryBlock`, before its first instruction. The emitted instructions must
    // preserve every value live-in to the entry block (the calling-convention
    // argument bytes) — the target is responsible for that (e.g. on the MOS6502,
    // shuttling a live `$a` through `$y` while `$a` is borrowed for the spill
    // loop). `builder` is positioned by the caller; the implementation sets its
    // own insertion point.
    public abstract void EmitCalleeSavedSpills(
        MirBlock entryBlock, IReadOnlyList<int> saved, MirBuilder builder);

    // Emit the epilogue restore sequence for `saved` immediately BEFORE
    // `returnInstr`. The same registers as EmitCalleeSavedSpills, but restored in
    // the reverse (LIFO) order — the target restores in DESCENDING order to match
    // a stack discipline. The emitted instructions must preserve any value the
    // return instruction itself uses (the return-value bytes carried as implicit
    // uses on the return).
    public abstract void EmitCalleeSavedRestores(
        MirInstruction returnInstr, IReadOnlyList<int> saved, MirBuilder builder);
}
