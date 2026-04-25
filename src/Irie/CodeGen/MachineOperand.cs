namespace Irie.CodeGen;

public abstract record MachineOperand;

public sealed record VirtualRegisterOperand(int VirtualRegister, bool IsDefinition) : MachineOperand;

public sealed record PhysicalRegisterOperand(int Register, bool IsDefinition) : MachineOperand;

public sealed record ImmediateOperand(long Value) : MachineOperand;

public sealed record BlockOperand(MachineBasicBlock Block) : MachineOperand;

public sealed record ExternalSymbolOperand(string Name) : MachineOperand;
