using Irie.CodeGen;
using Irie.IR;

namespace Irie.Target.MOS6502;

public sealed class MOS6502LegalizerInfo : Irie.CodeGen.LegalizerInfo
{
    public override Irie.CodeGen.LegalityAction GetAction(int opcode, IRType type)
    {
        if (type is not IntegerType intType)
            return Irie.CodeGen.LegalityAction.Unsupported;

        return opcode switch
        {
            // i8 arithmetic is native.
            GenericOpcode.GenericAdd when intType.SizeInBits == 8  => Irie.CodeGen.LegalityAction.Legal,
            // i32 (and other multi-byte widths) must be split into i8 operations.
            GenericOpcode.GenericAdd when intType.SizeInBits > 8   => Irie.CodeGen.LegalityAction.NarrowScalar,

            // Helper opcodes used during legalization are always legal.
            GenericOpcode.GenericAddCarry         => Irie.CodeGen.LegalityAction.Legal,
            GenericOpcode.GenericMerge            => Irie.CodeGen.LegalityAction.Legal,
            GenericOpcode.GenericUnmerge          => Irie.CodeGen.LegalityAction.Legal,
            GenericOpcode.GenericCopy             => Irie.CodeGen.LegalityAction.Legal,
            // i1 constants are materialized via LDImm1 by the selector.
            GenericOpcode.GenericConstant when intType.SizeInBits == 1 => Irie.CodeGen.LegalityAction.Legal,

            _ => Irie.CodeGen.LegalityAction.Unsupported,
        };
    }

    public override IRType GetNarrowType(int opcode, IRType fromType) =>
        opcode == GenericOpcode.GenericAdd ? IRType.I8
        : throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no narrow type defined for opcode {opcode}");
}
