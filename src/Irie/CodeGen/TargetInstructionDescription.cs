namespace Irie.CodeGen;

public abstract record TargetInstructionDescription(
    int    Opcode,
    string Mnemonic,
    int    Size,
    int[]? OperandClasses = null,
    int[]? TiedOperands   = null,
    int[]? ImplicitDefs   = null,
    int[]? ImplicitUses   = null)
{
    public int GetTiedToIndex(int operandIdx) =>
        TiedOperands != null && operandIdx < TiedOperands.Length
            ? TiedOperands[operandIdx]
            : -1;
}
