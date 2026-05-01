using Irie.CodeGen;
using Irie.IR;

namespace Irie.Target.MOS6502;

public sealed class MOS6502LegalizerInfo : LegalizerInfo
{
    public override LegalityAction GetAction(int opcode, IRType type)
    {
        if (type is not IntegerType intType)
            return LegalityAction.Unsupported;

        return opcode switch
        {
            // i8 arithmetic is native.
            GenericOpcode.GenericAdd when intType.SizeInBits == 8  => LegalityAction.Legal,
            // i32 (and other multi-byte widths) must be split into i8 operations.
            GenericOpcode.GenericAdd when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,

            // Helper opcodes used during legalization are always legal.
            GenericOpcode.GenericAddCarry         => LegalityAction.Legal,
            GenericOpcode.GenericMerge            => LegalityAction.Legal,
            GenericOpcode.GenericUnmerge          => LegalityAction.Legal,
            GenericOpcode.GenericCopy             => LegalityAction.Legal,

            _ => LegalityAction.Unsupported,
        };
    }

    public override IRType GetNarrowType(int opcode, IRType fromType) =>
        opcode == GenericOpcode.GenericAdd ? IRType.I8
        : throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no narrow type defined for opcode {opcode}");
}
