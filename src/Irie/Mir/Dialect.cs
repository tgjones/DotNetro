namespace Irie.Mir;

// A dialect bundles a prefix string with an opcode enum and per-opcode
// metadata. Dialects are looked up by ID (an opaque DialectId handed out by
// DialectRegistry.Register at registration time) or by their prefix.
public abstract class Dialect
{
    // Populated by DialectRegistry.Register. Stays at default(DialectId)
    // until then; do not look up a dialect by Id before it's registered.
    public DialectId Id { get; internal set; }

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

    // True when this opcode returns from the function (e.g. `mos6502.rts`).
    // Mirrors LLVM's MCInstrDesc::isReturn — a generic, target-agnostic query
    // used by passes such as TailDuplicationPass. Scaffolding/non-target
    // dialects have no return op, so the default is false.
    public virtual bool IsReturn(ushort code) => false;

    // True when this opcode is a type-shape artifact (e.g. pseudo.merge /
    // pseudo.unmerge) that the legalization artifact combiner can fold.
    public abstract bool IsArtifact(ushort code);

    // Optional symbolic rendering for an Immediate use operand. Returns true
    // and sets `text` if the dialect wants to render the immediate symbolically
    // (e.g. `arith.cmpi`'s first use is a predicate enum value rendered as
    // `slt`, `eq`, etc.); returns false to fall back to numeric formatting.
    // `useIndex` is the index of the operand among the instruction's uses
    // (after the defs).
    public virtual bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        text = string.Empty;
        return false;
    }

    // Inverse of TryFormatImmediateUse. Returns true and sets `value` when the
    // dialect can interpret `text` as a symbolic immediate at the given use
    // position; returns false to let the parser try other interpretations.
    public virtual bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        value = 0;
        return false;
    }

    // Called by DialectRegistry.Register exactly once when the dialect is
    // registered. The implementation stores the assigned ID in its own static
    // slot — the registry never references a dialect class by name.
    internal abstract void OnRegistered(DialectId id);
}
