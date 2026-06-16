namespace Irie.MachineCode;

// Selects which byte of an ExternalRef the instruction encodes:
// `Full` is the full multi-byte address (default; used by JSR/JMP and most
// loads); `LowByte` and `HighByte` are the low/high half of the address,
// emitted as a single immediate byte. The low/high forms are what MOS6502
// assembly traditionally writes as `#<sym` and `#>sym`.
public enum SymbolHalf
{
    Full,
    LowByte,
    HighByte,
}

public abstract record MachineCodeOperand
{
    public sealed record Register(int RegNum) : MachineCodeOperand;
    public sealed record Immediate(long Value) : MachineCodeOperand;
    public sealed record LabelRef(string Name) : MachineCodeOperand;
    public sealed record ExternalRef(string Name, SymbolHalf Half = SymbolHalf.Full, int Offset = 0) : MachineCodeOperand;
}
