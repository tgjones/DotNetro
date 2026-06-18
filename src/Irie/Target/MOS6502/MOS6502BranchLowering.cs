using Irie.Mir;

namespace Irie.Target.MOS6502;

// After instruction selection an unconditional branch is `mos6502.jmp.abs`, so
// a split critical-edge block created post-isel by PhiEliminationPass uses it
// (a generic `cf.br` would reach machine-code emission unlowered). See
// BranchLowering.
public sealed class MOS6502BranchLowering : Irie.Target.BranchLowering
{
    public override void InsertUnconditionalBranch(MirBuilder builder, BlockTarget target) =>
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JmpAbs), target);

    public override bool TryGetUnconditionalBranchTarget(MirInstruction instr, out MirBlock target)
    {
        target = null!;
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;
        if ((MOS6502Op)instr.Opcode.Code != MOS6502Op.JmpAbs) return false;
        if (instr.Operands.Length != 1) return false;
        if (instr.Operands[0] is not BlockTarget bt) return false;
        target = bt.Block;
        return true;
    }
}
