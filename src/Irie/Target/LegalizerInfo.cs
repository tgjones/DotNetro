using Irie.Mir;

namespace Irie.Target;

// MIR-shaped legalizer information. Per (opcode, type) pair, the target reports
// whether the op is legal, must be narrowed, or is unsupported. The legalizer
// queries this for every instruction it sees and drives the corresponding
// transformation (only NarrowScalar today). Mirrors the old
// Irie.CodeGen.LegalizerInfo, but talks in OpcodeRef + Mir types.
public abstract class LegalizerInfo
{
    // Returns the legality of an (opcode, type) combination. The type is the
    // annotation of the instruction's first def operand.
    public abstract LegalityAction GetAction(OpcodeRef opcode, IRType type);

    // Returns the type to narrow to for a NarrowScalar action.
    public abstract IRType GetNarrowType(OpcodeRef opcode, IRType fromType);

    // Hook for the Custom legality action: the target rewrites the instruction
    // via the builder (and removes the original). The builder is positioned at
    // the instruction on entry. Throws if the target doesn't recognise the op.
    public virtual void LegalizeCustom(MirInstruction instruction, MirBuilder builder)
    {
        throw new NotImplementedException(
            "LegalizerInfo.LegalizeCustom must be overridden by the target for any opcode that " +
            "returns LegalityAction.Custom.");
    }
}

public enum LegalityAction
{
    Legal,
    NarrowScalar,
    // Target-supplied transformation: the legalizer calls
    // LegalizerInfo.LegalizeCustom which mutates the IR via the builder.
    Custom,
    Unsupported,
}
