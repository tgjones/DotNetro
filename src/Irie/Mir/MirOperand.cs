namespace Irie.Mir;

public abstract record MirOperand;

public sealed record VirtualReg(int Id, bool IsDefinition) : MirOperand;

public sealed record PhysicalReg(int Id, bool IsDefinition, bool IsImplicit = false) : MirOperand;

public sealed record Immediate(long Value) : MirOperand;

// Successor block target on a terminator. Args carries the block-argument
// operands (typically VirtualReg uses); empty after RA has eliminated block
// parameters.
public sealed record BlockTarget(MirBlock Block, MirOperand[] Args) : MirOperand;

public sealed record Symbol(string Name) : MirOperand;
