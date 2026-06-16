namespace Irie.Mir;

public abstract record MirOperand;

public sealed record VirtualReg(int Id, bool IsDefinition) : MirOperand;

public sealed record PhysicalReg(int Id, bool IsDefinition, bool IsImplicit = false) : MirOperand;

public sealed record Immediate(long Value) : MirOperand;

// Successor block target on a terminator. Args carries the block-argument
// operands (typically VirtualReg uses); empty after RA has eliminated block
// parameters.
public sealed record BlockTarget(MirBlock Block, MirOperand[] Args) : MirOperand;

// A named symbol reference, optionally with a byte Offset added to the
// symbol's address (e.g. `@counter+1` to address the high byte of an i16
// global). Offset defaults to 0; the writer omits `+0` so existing goldens
// are unchanged.
public sealed record Symbol(string Name, int Offset = 0) : MirOperand;
