namespace Irie.Target.MOS6502;

// Byte-opcode (0..0xFF) → (mnemonic, addressing mode) table used by
// MOS6502AssemblyParser / MOS6502AssemblyWriter for the human-readable
// assembly text format. The legacy CodeGen-pipeline TargetInstructionInfo
// base has been retired (per unified-IR plan §10 step 17); this class is now
// a self-contained lookup table for the MachineCode text emitter only.
public sealed class MOS6502InstructionInfo
{
    public static readonly MOS6502InstructionInfo Instance = new();

    private static readonly Dictionary<int, MOS6502InstructionDescription> Table = BuildTable();

    public MOS6502InstructionDescription? TryGet(int opcode)
        => Table.TryGetValue(opcode, out var info) ? info : null;

    public MOS6502InstructionDescription Get(int opcode)
        => Table.TryGetValue(opcode, out var info) ? info
        : throw new ArgumentException($"Unknown opcode: ${opcode:X2}", nameof(opcode));

    private static Dictionary<int, MOS6502InstructionDescription> BuildTable()
    {
        var entries = new MOS6502InstructionDescription[]
        {
            // LDA
            new(MOS6502Opcode.LDA_Immediate, "LDA", AddressingMode.Immediate),
            new(MOS6502Opcode.LDA_ZeroPage,  "LDA", AddressingMode.ZeroPage),
            new(MOS6502Opcode.LDA_ZeroPageX, "LDA", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.LDA_Absolute,  "LDA", AddressingMode.Absolute),
            new(MOS6502Opcode.LDA_AbsoluteX, "LDA", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.LDA_AbsoluteY, "LDA", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.LDA_IndirectX, "LDA", AddressingMode.IndirectX),
            new(MOS6502Opcode.LDA_IndirectY, "LDA", AddressingMode.IndirectY),
            // STA
            new(MOS6502Opcode.STA_ZeroPage,  "STA", AddressingMode.ZeroPage),
            new(MOS6502Opcode.STA_ZeroPageX, "STA", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.STA_Absolute,  "STA", AddressingMode.Absolute),
            new(MOS6502Opcode.STA_AbsoluteX, "STA", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.STA_AbsoluteY, "STA", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.STA_IndirectX, "STA", AddressingMode.IndirectX),
            new(MOS6502Opcode.STA_IndirectY, "STA", AddressingMode.IndirectY),
            // LDX
            new(MOS6502Opcode.LDX_Immediate, "LDX", AddressingMode.Immediate),
            new(MOS6502Opcode.LDX_ZeroPage,  "LDX", AddressingMode.ZeroPage),
            new(MOS6502Opcode.LDX_ZeroPageY, "LDX", AddressingMode.ZeroPageY),
            new(MOS6502Opcode.LDX_Absolute,  "LDX", AddressingMode.Absolute),
            new(MOS6502Opcode.LDX_AbsoluteY, "LDX", AddressingMode.AbsoluteY),
            // STX
            new(MOS6502Opcode.STX_ZeroPage,  "STX", AddressingMode.ZeroPage),
            new(MOS6502Opcode.STX_ZeroPageY, "STX", AddressingMode.ZeroPageY),
            new(MOS6502Opcode.STX_Absolute,  "STX", AddressingMode.Absolute),
            // LDY
            new(MOS6502Opcode.LDY_Immediate, "LDY", AddressingMode.Immediate),
            new(MOS6502Opcode.LDY_ZeroPage,  "LDY", AddressingMode.ZeroPage),
            new(MOS6502Opcode.LDY_ZeroPageX, "LDY", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.LDY_Absolute,  "LDY", AddressingMode.Absolute),
            new(MOS6502Opcode.LDY_AbsoluteX, "LDY", AddressingMode.AbsoluteX),
            // STY
            new(MOS6502Opcode.STY_ZeroPage,  "STY", AddressingMode.ZeroPage),
            new(MOS6502Opcode.STY_ZeroPageX, "STY", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.STY_Absolute,  "STY", AddressingMode.Absolute),
            // ADC
            new(MOS6502Opcode.ADC_Immediate, "ADC", AddressingMode.Immediate),
            new(MOS6502Opcode.ADC_ZeroPage,  "ADC", AddressingMode.ZeroPage),
            new(MOS6502Opcode.ADC_ZeroPageX, "ADC", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.ADC_Absolute,  "ADC", AddressingMode.Absolute),
            new(MOS6502Opcode.ADC_AbsoluteX, "ADC", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.ADC_AbsoluteY, "ADC", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.ADC_IndirectX, "ADC", AddressingMode.IndirectX),
            new(MOS6502Opcode.ADC_IndirectY, "ADC", AddressingMode.IndirectY),
            // SBC
            new(MOS6502Opcode.SBC_Immediate, "SBC", AddressingMode.Immediate),
            new(MOS6502Opcode.SBC_ZeroPage,  "SBC", AddressingMode.ZeroPage),
            new(MOS6502Opcode.SBC_ZeroPageX, "SBC", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.SBC_Absolute,  "SBC", AddressingMode.Absolute),
            new(MOS6502Opcode.SBC_AbsoluteX, "SBC", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.SBC_AbsoluteY, "SBC", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.SBC_IndirectX, "SBC", AddressingMode.IndirectX),
            new(MOS6502Opcode.SBC_IndirectY, "SBC", AddressingMode.IndirectY),
            // AND
            new(MOS6502Opcode.AND_Immediate, "AND", AddressingMode.Immediate),
            new(MOS6502Opcode.AND_ZeroPage,  "AND", AddressingMode.ZeroPage),
            new(MOS6502Opcode.AND_ZeroPageX, "AND", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.AND_Absolute,  "AND", AddressingMode.Absolute),
            new(MOS6502Opcode.AND_AbsoluteX, "AND", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.AND_AbsoluteY, "AND", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.AND_IndirectX, "AND", AddressingMode.IndirectX),
            new(MOS6502Opcode.AND_IndirectY, "AND", AddressingMode.IndirectY),
            // ORA
            new(MOS6502Opcode.ORA_Immediate, "ORA", AddressingMode.Immediate),
            new(MOS6502Opcode.ORA_ZeroPage,  "ORA", AddressingMode.ZeroPage),
            new(MOS6502Opcode.ORA_ZeroPageX, "ORA", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.ORA_Absolute,  "ORA", AddressingMode.Absolute),
            new(MOS6502Opcode.ORA_AbsoluteX, "ORA", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.ORA_AbsoluteY, "ORA", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.ORA_IndirectX, "ORA", AddressingMode.IndirectX),
            new(MOS6502Opcode.ORA_IndirectY, "ORA", AddressingMode.IndirectY),
            // EOR
            new(MOS6502Opcode.EOR_Immediate, "EOR", AddressingMode.Immediate),
            new(MOS6502Opcode.EOR_ZeroPage,  "EOR", AddressingMode.ZeroPage),
            new(MOS6502Opcode.EOR_ZeroPageX, "EOR", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.EOR_Absolute,  "EOR", AddressingMode.Absolute),
            new(MOS6502Opcode.EOR_AbsoluteX, "EOR", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.EOR_AbsoluteY, "EOR", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.EOR_IndirectX, "EOR", AddressingMode.IndirectX),
            new(MOS6502Opcode.EOR_IndirectY, "EOR", AddressingMode.IndirectY),
            // CMP
            new(MOS6502Opcode.CMP_Immediate, "CMP", AddressingMode.Immediate),
            new(MOS6502Opcode.CMP_ZeroPage,  "CMP", AddressingMode.ZeroPage),
            new(MOS6502Opcode.CMP_ZeroPageX, "CMP", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.CMP_Absolute,  "CMP", AddressingMode.Absolute),
            new(MOS6502Opcode.CMP_AbsoluteX, "CMP", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.CMP_AbsoluteY, "CMP", AddressingMode.AbsoluteY),
            new(MOS6502Opcode.CMP_IndirectX, "CMP", AddressingMode.IndirectX),
            new(MOS6502Opcode.CMP_IndirectY, "CMP", AddressingMode.IndirectY),
            // CPX / CPY
            new(MOS6502Opcode.CPX_Immediate, "CPX", AddressingMode.Immediate),
            new(MOS6502Opcode.CPX_ZeroPage,  "CPX", AddressingMode.ZeroPage),
            new(MOS6502Opcode.CPX_Absolute,  "CPX", AddressingMode.Absolute),
            new(MOS6502Opcode.CPY_Immediate, "CPY", AddressingMode.Immediate),
            new(MOS6502Opcode.CPY_ZeroPage,  "CPY", AddressingMode.ZeroPage),
            new(MOS6502Opcode.CPY_Absolute,  "CPY", AddressingMode.Absolute),
            // INC / DEC
            new(MOS6502Opcode.INC_ZeroPage,  "INC", AddressingMode.ZeroPage),
            new(MOS6502Opcode.INC_ZeroPageX, "INC", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.INC_Absolute,  "INC", AddressingMode.Absolute),
            new(MOS6502Opcode.INC_AbsoluteX, "INC", AddressingMode.AbsoluteX),
            new(MOS6502Opcode.DEC_ZeroPage,  "DEC", AddressingMode.ZeroPage),
            new(MOS6502Opcode.DEC_ZeroPageX, "DEC", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.DEC_Absolute,  "DEC", AddressingMode.Absolute),
            new(MOS6502Opcode.DEC_AbsoluteX, "DEC", AddressingMode.AbsoluteX),
            // Register inc/dec
            new(MOS6502Opcode.INX, "INX", AddressingMode.Implied),
            new(MOS6502Opcode.DEX, "DEX", AddressingMode.Implied),
            new(MOS6502Opcode.INY, "INY", AddressingMode.Implied),
            new(MOS6502Opcode.DEY, "DEY", AddressingMode.Implied),
            // ASL
            new(MOS6502Opcode.ASL_Accumulator, "ASL", AddressingMode.Implied),
            new(MOS6502Opcode.ASL_ZeroPage,    "ASL", AddressingMode.ZeroPage),
            new(MOS6502Opcode.ASL_ZeroPageX,   "ASL", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.ASL_Absolute,    "ASL", AddressingMode.Absolute),
            new(MOS6502Opcode.ASL_AbsoluteX,   "ASL", AddressingMode.AbsoluteX),
            // LSR
            new(MOS6502Opcode.LSR_Accumulator, "LSR", AddressingMode.Implied),
            new(MOS6502Opcode.LSR_ZeroPage,    "LSR", AddressingMode.ZeroPage),
            new(MOS6502Opcode.LSR_ZeroPageX,   "LSR", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.LSR_Absolute,    "LSR", AddressingMode.Absolute),
            new(MOS6502Opcode.LSR_AbsoluteX,   "LSR", AddressingMode.AbsoluteX),
            // ROL
            new(MOS6502Opcode.ROL_Accumulator, "ROL", AddressingMode.Implied),
            new(MOS6502Opcode.ROL_ZeroPage,    "ROL", AddressingMode.ZeroPage),
            new(MOS6502Opcode.ROL_ZeroPageX,   "ROL", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.ROL_Absolute,    "ROL", AddressingMode.Absolute),
            new(MOS6502Opcode.ROL_AbsoluteX,   "ROL", AddressingMode.AbsoluteX),
            // ROR
            new(MOS6502Opcode.ROR_Accumulator, "ROR", AddressingMode.Implied),
            new(MOS6502Opcode.ROR_ZeroPage,    "ROR", AddressingMode.ZeroPage),
            new(MOS6502Opcode.ROR_ZeroPageX,   "ROR", AddressingMode.ZeroPageX),
            new(MOS6502Opcode.ROR_Absolute,    "ROR", AddressingMode.Absolute),
            new(MOS6502Opcode.ROR_AbsoluteX,   "ROR", AddressingMode.AbsoluteX),
            // Branches
            new(MOS6502Opcode.BEQ, "BEQ", AddressingMode.Relative),
            new(MOS6502Opcode.BNE, "BNE", AddressingMode.Relative),
            new(MOS6502Opcode.BCC, "BCC", AddressingMode.Relative),
            new(MOS6502Opcode.BCS, "BCS", AddressingMode.Relative),
            new(MOS6502Opcode.BMI, "BMI", AddressingMode.Relative),
            new(MOS6502Opcode.BPL, "BPL", AddressingMode.Relative),
            new(MOS6502Opcode.BVC, "BVC", AddressingMode.Relative),
            new(MOS6502Opcode.BVS, "BVS", AddressingMode.Relative),
            // Jumps / calls
            new(MOS6502Opcode.JMP_Absolute, "JMP", AddressingMode.Absolute),
            new(MOS6502Opcode.JMP_Indirect, "JMP", AddressingMode.Indirect),
            new(MOS6502Opcode.JSR_Absolute, "JSR", AddressingMode.Absolute),
            new(MOS6502Opcode.RTS,          "RTS", AddressingMode.Implied),
            new(MOS6502Opcode.RTI,          "RTI", AddressingMode.Implied),
            // Stack
            new(MOS6502Opcode.PHA, "PHA", AddressingMode.Implied),
            new(MOS6502Opcode.PLA, "PLA", AddressingMode.Implied),
            new(MOS6502Opcode.PHP, "PHP", AddressingMode.Implied),
            new(MOS6502Opcode.PLP, "PLP", AddressingMode.Implied),
            // Transfers
            new(MOS6502Opcode.TAX, "TAX", AddressingMode.Implied),
            new(MOS6502Opcode.TXA, "TXA", AddressingMode.Implied),
            new(MOS6502Opcode.TAY, "TAY", AddressingMode.Implied),
            new(MOS6502Opcode.TYA, "TYA", AddressingMode.Implied),
            new(MOS6502Opcode.TSX, "TSX", AddressingMode.Implied),
            new(MOS6502Opcode.TXS, "TXS", AddressingMode.Implied),
            // Flags
            new(MOS6502Opcode.CLC, "CLC", AddressingMode.Implied),
            new(MOS6502Opcode.SEC, "SEC", AddressingMode.Implied),
            new(MOS6502Opcode.CLI, "CLI", AddressingMode.Implied),
            new(MOS6502Opcode.SEI, "SEI", AddressingMode.Implied),
            new(MOS6502Opcode.CLV, "CLV", AddressingMode.Implied),
            new(MOS6502Opcode.CLD, "CLD", AddressingMode.Implied),
            new(MOS6502Opcode.SED, "SED", AddressingMode.Implied),
            // Misc
            new(MOS6502Opcode.NOP, "NOP", AddressingMode.Implied),
            new(MOS6502Opcode.BRK, "BRK", AddressingMode.Implied),
        };

        var result = new Dictionary<int, MOS6502InstructionDescription>(entries.Length);
        foreach (var entry in entries)
            result.Add(entry.Opcode, entry);
        return result;
    }
}

public sealed record MOS6502InstructionDescription(
    int            Opcode,
    string         Mnemonic,
    AddressingMode Mode);
