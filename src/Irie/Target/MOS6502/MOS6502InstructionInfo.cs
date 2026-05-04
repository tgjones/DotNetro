namespace Irie.Target.MOS6502;

public sealed class MOS6502InstructionInfo(
    int opcode,
    string mnemonic,
    AddressingMode mode,
    int size,
    int[]? operandClasses = null,
    int[]? tiedOperands = null,
    int[]? implicitDefs = null,
    int[]? implicitUses = null)
{
    public int Opcode { get; } = opcode;
    public string Mnemonic { get; } = mnemonic;
    public AddressingMode Mode { get; } = mode;

    /// <summary>Total instruction size in bytes including the opcode byte.</summary>
    public int Size { get; } = size;

    /// <summary>
    /// Per-operand register class constraints (defs first, then uses), indexed
    /// by position in <see cref="MachineInstruction.Operands"/>. Use
    /// <see cref="MOS6502RegisterClass.None"/> (0) for positions that don't carry
    /// a register-class constraint (e.g. immediates). Null for instructions whose
    /// operands have no class constraints recorded yet.
    /// </summary>
    public int[]? OperandClasses { get; } = operandClasses;

    /// <summary>
    /// Tied-operand pairs: parallel to <see cref="OperandClasses"/>; each entry
    /// is the index of the operand this operand is tied to (defs-first layout),
    /// or -1 if not tied. Null means no tied operands.
    /// </summary>
    public int[]? TiedOperands { get; } = tiedOperands;

    /// <summary>
    /// Returns the operand index that operand at <paramref name="operandIdx"/> is
    /// tied to, or -1 if not tied.
    /// </summary>
    public int GetTiedToIndex(int operandIdx) =>
        TiedOperands != null && operandIdx < TiedOperands.Length
            ? TiedOperands[operandIdx]
            : -1;

    /// <summary>
    /// Physical registers implicitly defined (clobbered) by this opcode, beyond
    /// those already expressed as explicit def operands. Null means none.
    /// </summary>
    public int[]? ImplicitDefs { get; } = implicitDefs;

    /// <summary>
    /// Physical registers implicitly used (read) by this opcode, beyond those
    /// already expressed as explicit use operands. Null means none.
    /// </summary>
    public int[]? ImplicitUses { get; } = implicitUses;

    private static readonly Dictionary<int, MOS6502InstructionInfo> _table = BuildTable();

    public static MOS6502InstructionInfo? TryGet(int opcode)
        => _table.TryGetValue(opcode, out var info) ? info : null;

    public static MOS6502InstructionInfo Get(int opcode)
        => _table.TryGetValue(opcode, out var info) ? info
        : throw new ArgumentException($"Unknown opcode: ${opcode:X2}", nameof(opcode));

    // Returns a human-readable name suitable for MachineIR text output, e.g. "ADC_ZeroPage".
    // Pseudo-mode opcodes use just the mnemonic (e.g. "LDImm1").
    public static string? GetDisplayName(int opcode)
    {
        var info = TryGet(opcode);
        if (info == null) return null;
        return info.Mode == AddressingMode.Pseudo
            ? info.Mnemonic
            : $"{info.Mnemonic}_{info.Mode}";
    }

    // Parses a display name back to an opcode. Inverse of GetDisplayName.
    public static int? ParseDisplayName(string name)
    {
        foreach (var info in _table.Values)
        {
            var displayName = info.Mode == AddressingMode.Pseudo
                ? info.Mnemonic
                : $"{info.Mnemonic}_{info.Mode}";
            if (displayName == name)
                return info.Opcode;
        }
        return null;
    }

    private static Dictionary<int, MOS6502InstructionInfo> BuildTable()
    {
        var entries = new MOS6502InstructionInfo[]
        {
            // LDA
            new(MOS6502Opcode.LDA_Immediate, "LDA", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.LDA_ZeroPage,  "LDA", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.LDA_ZeroPageX, "LDA", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.LDA_Absolute,  "LDA", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.LDA_AbsoluteX, "LDA", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.LDA_AbsoluteY, "LDA", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.LDA_IndirectX, "LDA", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.LDA_IndirectY, "LDA", AddressingMode.IndirectY, 2),
            // STA
            new(MOS6502Opcode.STA_ZeroPage,  "STA", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.STA_ZeroPageX, "STA", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.STA_Absolute,  "STA", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.STA_AbsoluteX, "STA", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.STA_AbsoluteY, "STA", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.STA_IndirectX, "STA", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.STA_IndirectY, "STA", AddressingMode.IndirectY, 2),
            // LDX
            new(MOS6502Opcode.LDX_Immediate, "LDX", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.LDX_ZeroPage,  "LDX", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.LDX_ZeroPageY, "LDX", AddressingMode.ZeroPageY, 2),
            new(MOS6502Opcode.LDX_Absolute,  "LDX", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.LDX_AbsoluteY, "LDX", AddressingMode.AbsoluteY, 3),
            // STX
            new(MOS6502Opcode.STX_ZeroPage,  "STX", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.STX_ZeroPageY, "STX", AddressingMode.ZeroPageY, 2),
            new(MOS6502Opcode.STX_Absolute,  "STX", AddressingMode.Absolute,  3),
            // LDY
            new(MOS6502Opcode.LDY_Immediate, "LDY", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.LDY_ZeroPage,  "LDY", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.LDY_ZeroPageX, "LDY", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.LDY_Absolute,  "LDY", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.LDY_AbsoluteX, "LDY", AddressingMode.AbsoluteX, 3),
            // STY
            new(MOS6502Opcode.STY_ZeroPage,  "STY", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.STY_ZeroPageX, "STY", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.STY_Absolute,  "STY", AddressingMode.Absolute,  3),
            // ADC
            new(MOS6502Opcode.ADC_Immediate, "ADC", AddressingMode.Immediate, 2),
            // ADC against a zero-page (imaginary) byte: defs are (result, carry_out)
            // and uses are (L, R, carry_in). We don't currently model the V flag def,
            // so the operand layout is [Ac, Cc, Ac, Imag8, Cc].
            new(MOS6502Opcode.ADC_ZeroPage,  "ADC", AddressingMode.ZeroPage,  2,
                operandClasses: [
                    MOS6502RegisterClass.Ac,    // def[0]: result
                    MOS6502RegisterClass.Cc,    // def[1]: carry_out
                    MOS6502RegisterClass.Ac,    // use[0]: L
                    MOS6502RegisterClass.Imag8, // use[1]: R
                    MOS6502RegisterClass.Cc,    // use[2]: carry_in
                ],
                tiedOperands: [-1, -1, 0, -1, -1]), // use[0] (pos 2) tied to def[0] (pos 0)
            new(MOS6502Opcode.ADC_ZeroPageX, "ADC", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.ADC_Absolute,  "ADC", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.ADC_AbsoluteX, "ADC", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.ADC_AbsoluteY, "ADC", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.ADC_IndirectX, "ADC", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.ADC_IndirectY, "ADC", AddressingMode.IndirectY, 2),
            // SBC
            new(MOS6502Opcode.SBC_Immediate, "SBC", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.SBC_ZeroPage,  "SBC", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.SBC_ZeroPageX, "SBC", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.SBC_Absolute,  "SBC", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.SBC_AbsoluteX, "SBC", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.SBC_AbsoluteY, "SBC", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.SBC_IndirectX, "SBC", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.SBC_IndirectY, "SBC", AddressingMode.IndirectY, 2),
            // AND
            new(MOS6502Opcode.AND_Immediate, "AND", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.AND_ZeroPage,  "AND", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.AND_ZeroPageX, "AND", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.AND_Absolute,  "AND", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.AND_AbsoluteX, "AND", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.AND_AbsoluteY, "AND", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.AND_IndirectX, "AND", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.AND_IndirectY, "AND", AddressingMode.IndirectY, 2),
            // ORA
            new(MOS6502Opcode.ORA_Immediate, "ORA", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.ORA_ZeroPage,  "ORA", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.ORA_ZeroPageX, "ORA", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.ORA_Absolute,  "ORA", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.ORA_AbsoluteX, "ORA", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.ORA_AbsoluteY, "ORA", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.ORA_IndirectX, "ORA", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.ORA_IndirectY, "ORA", AddressingMode.IndirectY, 2),
            // EOR
            new(MOS6502Opcode.EOR_Immediate, "EOR", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.EOR_ZeroPage,  "EOR", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.EOR_ZeroPageX, "EOR", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.EOR_Absolute,  "EOR", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.EOR_AbsoluteX, "EOR", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.EOR_AbsoluteY, "EOR", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.EOR_IndirectX, "EOR", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.EOR_IndirectY, "EOR", AddressingMode.IndirectY, 2),
            // CMP
            new(MOS6502Opcode.CMP_Immediate, "CMP", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.CMP_ZeroPage,  "CMP", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.CMP_ZeroPageX, "CMP", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.CMP_Absolute,  "CMP", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.CMP_AbsoluteX, "CMP", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.CMP_AbsoluteY, "CMP", AddressingMode.AbsoluteY, 3),
            new(MOS6502Opcode.CMP_IndirectX, "CMP", AddressingMode.IndirectX, 2),
            new(MOS6502Opcode.CMP_IndirectY, "CMP", AddressingMode.IndirectY, 2),
            // CPX / CPY
            new(MOS6502Opcode.CPX_Immediate, "CPX", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.CPX_ZeroPage,  "CPX", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.CPX_Absolute,  "CPX", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.CPY_Immediate, "CPY", AddressingMode.Immediate, 2),
            new(MOS6502Opcode.CPY_ZeroPage,  "CPY", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.CPY_Absolute,  "CPY", AddressingMode.Absolute,  3),
            // INC / DEC
            new(MOS6502Opcode.INC_ZeroPage,  "INC", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.INC_ZeroPageX, "INC", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.INC_Absolute,  "INC", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.INC_AbsoluteX, "INC", AddressingMode.AbsoluteX, 3),
            new(MOS6502Opcode.DEC_ZeroPage,  "DEC", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.DEC_ZeroPageX, "DEC", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.DEC_Absolute,  "DEC", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.DEC_AbsoluteX, "DEC", AddressingMode.AbsoluteX, 3),
            // Register inc/dec
            new(MOS6502Opcode.INX, "INX", AddressingMode.Implied, 1),
            new(MOS6502Opcode.DEX, "DEX", AddressingMode.Implied, 1),
            new(MOS6502Opcode.INY, "INY", AddressingMode.Implied, 1),
            new(MOS6502Opcode.DEY, "DEY", AddressingMode.Implied, 1),
            // ASL
            new(MOS6502Opcode.ASL_Accumulator, "ASL", AddressingMode.Implied,   1),
            new(MOS6502Opcode.ASL_ZeroPage,    "ASL", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.ASL_ZeroPageX,   "ASL", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.ASL_Absolute,    "ASL", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.ASL_AbsoluteX,   "ASL", AddressingMode.AbsoluteX, 3),
            // LSR
            new(MOS6502Opcode.LSR_Accumulator, "LSR", AddressingMode.Implied,   1),
            new(MOS6502Opcode.LSR_ZeroPage,    "LSR", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.LSR_ZeroPageX,   "LSR", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.LSR_Absolute,    "LSR", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.LSR_AbsoluteX,   "LSR", AddressingMode.AbsoluteX, 3),
            // ROL
            new(MOS6502Opcode.ROL_Accumulator, "ROL", AddressingMode.Implied,   1),
            new(MOS6502Opcode.ROL_ZeroPage,    "ROL", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.ROL_ZeroPageX,   "ROL", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.ROL_Absolute,    "ROL", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.ROL_AbsoluteX,   "ROL", AddressingMode.AbsoluteX, 3),
            // ROR
            new(MOS6502Opcode.ROR_Accumulator, "ROR", AddressingMode.Implied,   1),
            new(MOS6502Opcode.ROR_ZeroPage,    "ROR", AddressingMode.ZeroPage,  2),
            new(MOS6502Opcode.ROR_ZeroPageX,   "ROR", AddressingMode.ZeroPageX, 2),
            new(MOS6502Opcode.ROR_Absolute,    "ROR", AddressingMode.Absolute,  3),
            new(MOS6502Opcode.ROR_AbsoluteX,   "ROR", AddressingMode.AbsoluteX, 3),
            // Branches
            new(MOS6502Opcode.BEQ, "BEQ", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BNE, "BNE", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BCC, "BCC", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BCS, "BCS", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BMI, "BMI", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BPL, "BPL", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BVC, "BVC", AddressingMode.Relative, 2),
            new(MOS6502Opcode.BVS, "BVS", AddressingMode.Relative, 2),
            // Jumps / calls
            new(MOS6502Opcode.JMP_Absolute, "JMP", AddressingMode.Absolute, 3),
            new(MOS6502Opcode.JMP_Indirect, "JMP", AddressingMode.Indirect, 3),
            new(MOS6502Opcode.JSR_Absolute, "JSR", AddressingMode.Absolute, 3),
            new(MOS6502Opcode.RTS,          "RTS", AddressingMode.Implied,  1),
            new(MOS6502Opcode.RTI,          "RTI", AddressingMode.Implied,  1),
            // Stack
            new(MOS6502Opcode.PHA, "PHA", AddressingMode.Implied, 1),
            new(MOS6502Opcode.PLA, "PLA", AddressingMode.Implied, 1),
            new(MOS6502Opcode.PHP, "PHP", AddressingMode.Implied, 1),
            new(MOS6502Opcode.PLP, "PLP", AddressingMode.Implied, 1),
            // Transfers
            new(MOS6502Opcode.TAX, "TAX", AddressingMode.Implied, 1),
            new(MOS6502Opcode.TXA, "TXA", AddressingMode.Implied, 1),
            new(MOS6502Opcode.TAY, "TAY", AddressingMode.Implied, 1),
            new(MOS6502Opcode.TYA, "TYA", AddressingMode.Implied, 1),
            new(MOS6502Opcode.TSX, "TSX", AddressingMode.Implied, 1),
            new(MOS6502Opcode.TXS, "TXS", AddressingMode.Implied, 1),
            // Flags
            new(MOS6502Opcode.CLC, "CLC", AddressingMode.Implied, 1),
            new(MOS6502Opcode.SEC, "SEC", AddressingMode.Implied, 1),
            new(MOS6502Opcode.CLI, "CLI", AddressingMode.Implied, 1),
            new(MOS6502Opcode.SEI, "SEI", AddressingMode.Implied, 1),
            new(MOS6502Opcode.CLV, "CLV", AddressingMode.Implied, 1),
            new(MOS6502Opcode.CLD, "CLD", AddressingMode.Implied, 1),
            new(MOS6502Opcode.SED, "SED", AddressingMode.Implied, 1),
            // Misc
            new(MOS6502Opcode.NOP, "NOP", AddressingMode.Implied, 1),
            new(MOS6502Opcode.BRK, "BRK", AddressingMode.Implied, 1),

            // Pseudos. Size = 0 because these don't directly emit any bytes; a
            // CodeGen → MachineCode lowering (TBD) expands each to the real opcodes.
            // Operand layout: [Cc:def, immediate]. Position 1 has no class because
            // it's an immediate, not a register.
            new(MOS6502Opcode.LDImm1, "LDImm1", AddressingMode.Pseudo, 0,
                operandClasses: [
                    MOS6502RegisterClass.Cc,   // def[0]
                    MOS6502RegisterClass.None, // immediate
                ]),
        };

        var table = new Dictionary<int, MOS6502InstructionInfo>(entries.Length);
        foreach (var entry in entries)
            table.Add(entry.Opcode, entry);
        return table;
    }
}
