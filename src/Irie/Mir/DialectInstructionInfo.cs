namespace Irie.Mir;

// Per-opcode metadata used by passes (operand classes, tied operands, implicit
// def/use lists). Mirrors today's TargetInstructionDescription but lives at the
// dialect level so any dialect — generic or target — can supply it.
//
// TypeOperandIndex (optional): index of the operand whose vreg type drives the
// legalizer's type query. Defaults to "first vreg def" when null. Used for ops
// whose def type is fixed (e.g. arith.cmpi always produces i1) but whose
// legalization decision is driven by an operand type (i8 cmpi vs i32 cmpi).
public sealed record DialectInstructionInfo(
    int[]? OperandClasses    = null,
    int[]? TiedOperands      = null,
    int[]? ImplicitDefs      = null,
    int[]? ImplicitUses      = null,
    int?   TypeOperandIndex  = null)
{
    public static readonly DialectInstructionInfo Empty = new();

    public int GetTiedToIndex(int operandIdx) =>
        TiedOperands != null && operandIdx < TiedOperands.Length
            ? TiedOperands[operandIdx]
            : -1;
}
