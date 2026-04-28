using Irie.IR;

namespace Irie.CodeGen;

public abstract class LegalizerInfo
{
    // Returns the legality of a (opcode, type) combination.
    // The type is taken from the instruction's first def operand.
    public abstract LegalityAction GetAction(int opcode, IRType type);

    // Returns the type to narrow to for a NarrowScalar action.
    public abstract IRType GetNarrowType(int opcode, IRType fromType);
}

public enum LegalityAction
{
    Legal,
    NarrowScalar,
    Unsupported,
}
