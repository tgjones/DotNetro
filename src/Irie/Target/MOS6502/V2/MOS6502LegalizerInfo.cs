using Irie.Dialects.Arith;
using Irie.Dialects.Pseudo;
using Irie.IR;
using Irie.Mir;

namespace Irie.Target.MOS6502.V2;

// MOS6502 legalizer for the unified-MIR pipeline. Mirrors the old
// Irie.Target.MOS6502.MOS6502LegalizerInfo while talking in OpcodeRef and the
// new dialect opcodes (arith.addi, arith.addi_with_carry, arith.constant,
// pseudo.merge, pseudo.unmerge, pseudo.copy).
public sealed class MOS6502LegalizerInfo : Irie.Target.LegalizerInfo
{
    public override LegalityAction GetAction(OpcodeRef opcode, IRType type)
    {
        if (type is not IntegerType intType)
            return LegalityAction.Unsupported;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return ((ArithOp)opcode.Code) switch
            {
                ArithOp.AddI      when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.AddI      when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                ArithOp.AddICarry => LegalityAction.Legal,
                ArithOp.Constant  when intType.SizeInBits == 1  => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return ((PseudoOp)opcode.Code) switch
            {
                PseudoOp.Copy    => LegalityAction.Legal,
                PseudoOp.Merge   => LegalityAction.Legal,
                PseudoOp.Unmerge => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        return LegalityAction.Unsupported;
    }

    public override IRType GetNarrowType(OpcodeRef opcode, IRType fromType)
    {
        if (opcode.Dialect == ArithDialect.Id && (ArithOp)opcode.Code == ArithOp.AddI)
            return IRType.I8;

        throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no narrow type defined for opcode {opcode}");
    }
}
