namespace Irie.Mir;

// A dialect bundles a prefix string with an opcode enum and per-opcode
// metadata. Dialects are looked up by ID (an opaque DialectId handed out by
// DialectRegistry.Register at registration time) or by their prefix.
public abstract class Dialect
{
    // Lowercase short name printed before each opcode (e.g. "arith", "core").
    public abstract string Prefix { get; }

    // Op name without the dialect prefix (e.g. "addi", "cmpi").
    public abstract string GetOpName(ushort code);

    // Reverse of GetOpName.
    public abstract bool TryParseOp(string name, out ushort code);

    // Per-opcode metadata. Returns DialectInstructionInfo.Empty for opcodes
    // that have no operand classes / tied operands / implicit operands.
    public abstract DialectInstructionInfo GetInstructionInfo(ushort code);

    // True when this opcode produces no observable side effects (drives DCE).
    public abstract bool IsSideEffectFree(ushort code);

    // True when this opcode terminates a basic block.
    public abstract bool IsTerminator(ushort code);

    // True when this opcode is a type-shape artifact (e.g. pseudo.merge /
    // pseudo.unmerge) that the legalization artifact combiner can fold.
    public abstract bool IsArtifact(ushort code);

    // Called by DialectRegistry.Register exactly once when the dialect is
    // registered. The implementation stores the assigned ID in its own static
    // slot — the registry never references a dialect class by name.
    internal abstract void OnRegistered(DialectId id);
}
