namespace Irie.Mir;

// Per-opcode metadata used by passes (operand classes, tied operands, implicit
// def/use lists). Mirrors today's TargetInstructionDescription but lives at the
// dialect level so any dialect — generic or target — can supply it.
public sealed record DialectInstructionInfo(
    int[]? OperandClasses = null,
    int[]? TiedOperands   = null,
    int[]? ImplicitDefs   = null,
    int[]? ImplicitUses   = null)
{
    public static readonly DialectInstructionInfo Empty = new();

    public int GetTiedToIndex(int operandIdx) =>
        TiedOperands != null && operandIdx < TiedOperands.Length
            ? TiedOperands[operandIdx]
            : -1;
}
