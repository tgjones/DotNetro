namespace Irie.Target.MOS6502;

// How a single MIR operand position translates into a MachineCode operand
// byte (or no operand at all). See notes/mir-to-machinecode-plan.md §2.2.
public enum EmitOperandKind
{
    // No operand byte. The instruction encodes as a single opcode byte; any
    // MIR operands are implicit (e.g. flag defs/uses, tied accumulator).
    Implied,

    // 1-byte zero-page address read from a PhysicalReg(zpN) operand. The
    // emitter computes the address byte as `phys.Id - MOS6502Registers.RC(0)`.
    ZeroPageAddress,

    // 1-byte immediate read from an Immediate operand.
    Immediate,

    // 1-byte signed branch offset. The MIR operand is a BlockTarget; the
    // emitter produces a MachineCodeOperand.LabelRef and leaves offset
    // resolution to the assembler.
    BranchTarget,

    // 2-byte absolute address. The MIR operand is either a Symbol (external
    // function name) or a BlockTarget (intra-function jump).
    AbsoluteAddress,
}

// Per-opcode rule: which 6502 byte to emit, how to interpret the operand,
// and which slot in the MIR operand array holds it (null for Implied).
public sealed record EmitRule(
    int             OpcodeByte,
    EmitOperandKind Kind,
    int?            OperandIndex);

// Table of MOS6502Op → EmitRule for the subset of opcodes the current
// instruction selector + addressing-mode selector + pseudo expander can
// actually produce. Get(op) throws for any opcode not yet covered — that
// failure is the signal that the table needs growing when isel grows.
//
// See notes/mir-to-machinecode-plan.md §2.2 for the operand-index reasoning
// behind each entry.
public static class MOS6502MachineCodeEmitTable
{
    private static readonly Dictionary<MOS6502Op, EmitRule> Table = new()
    {
        // $c = mos6502.clc — def[0] is the implicit-effect carry flag.
        [MOS6502Op.Clc]   = new EmitRule(MOS6502Opcode.CLC,          EmitOperandKind.Implied,         null),

        // $c = mos6502.sec — def[0] is the implicit-effect carry flag.
        // Used as the chain-head no-borrow for sbc chains.
        [MOS6502Op.Sec]   = new EmitRule(MOS6502Opcode.SEC,          EmitOperandKind.Implied,         null),

        // $a, $c = mos6502.adc.zp $a, $zpN, $c
        // Operands: def[0]=$a, def[1]=$c, use[0]=$a (tied), use[1]=zp, use[2]=$c.
        // Index 3 = use[1], the zero-page address.
        [MOS6502Op.AdcZp] = new EmitRule(MOS6502Opcode.ADC_ZeroPage, EmitOperandKind.ZeroPageAddress, 3),

        // $a, $c = mos6502.sbc.zp $a, $zpN, $c — same shape as AdcZp.
        [MOS6502Op.SbcZp] = new EmitRule(MOS6502Opcode.SBC_ZeroPage, EmitOperandKind.ZeroPageAddress, 3),

        // mos6502.cmp.zp $a, $zpN implicit-def $n, $z, $c
        // Operands: use[0]=$a (explicit), use[1]=zp (explicit), then implicit defs.
        // Index 1 = use[1], the zero-page address.
        [MOS6502Op.CmpZp] = new EmitRule(MOS6502Opcode.CMP_ZeroPage, EmitOperandKind.ZeroPageAddress, 1),

        // mos6502.cmp.imm $a, #N implicit-def $n, $z, $c
        // Index 1 = use[1] = immediate.
        [MOS6502Op.CmpImm] = new EmitRule(MOS6502Opcode.CMP_Immediate, EmitOperandKind.Immediate, 1),

        // mos6502.b<pred> T implicit $<flag>
        // operands[0] = BlockTarget T; operand[1] = implicit flag use (not encoded).
        [MOS6502Op.Beq]    = new EmitRule(MOS6502Opcode.BEQ,          EmitOperandKind.BranchTarget,    0),
        [MOS6502Op.Bne]    = new EmitRule(MOS6502Opcode.BNE,          EmitOperandKind.BranchTarget,    0),
        [MOS6502Op.Bcc]    = new EmitRule(MOS6502Opcode.BCC,          EmitOperandKind.BranchTarget,    0),
        [MOS6502Op.Bcs]    = new EmitRule(MOS6502Opcode.BCS,          EmitOperandKind.BranchTarget,    0),
        [MOS6502Op.Bmi]    = new EmitRule(MOS6502Opcode.BMI,          EmitOperandKind.BranchTarget,    0),
        [MOS6502Op.Bpl]    = new EmitRule(MOS6502Opcode.BPL,          EmitOperandKind.BranchTarget,    0),

        // mos6502.jmp.abs T — operands[0] = BlockTarget.
        [MOS6502Op.JmpAbs] = new EmitRule(MOS6502Opcode.JMP_Absolute, EmitOperandKind.AbsoluteAddress, 0),

        // mos6502.jsr.abs @callee — operands[0] = Symbol; rest are implicit.
        [MOS6502Op.JsrAbs] = new EmitRule(MOS6502Opcode.JSR_Absolute, EmitOperandKind.AbsoluteAddress, 0),

        // $zpN = mos6502.sta.zp $a
        // Operands: def[0]=zp (the address), use[0]=$a.
        // Index 0 = def[0]; the source register is implicit in the opcode.
        [MOS6502Op.StaZp] = new EmitRule(MOS6502Opcode.STA_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),

        // $a = mos6502.lda.zp $zpN
        // Operands: def[0]=$a, use[0]=zp. Index 1 = use[0].
        [MOS6502Op.LdaZp] = new EmitRule(MOS6502Opcode.LDA_ZeroPage, EmitOperandKind.ZeroPageAddress, 1),

        // $x = mos6502.ldx.zp $zpN — same shape as LdaZp.
        [MOS6502Op.LdxZp] = new EmitRule(MOS6502Opcode.LDX_ZeroPage, EmitOperandKind.ZeroPageAddress, 1),

        // $y = mos6502.ldy.zp $zpN — same shape as LdaZp/LdxZp.
        [MOS6502Op.LdyZp] = new EmitRule(MOS6502Opcode.LDY_ZeroPage, EmitOperandKind.ZeroPageAddress, 1),

        // $zpN = mos6502.stx.zp $x — same shape as StaZp.
        [MOS6502Op.StxZp] = new EmitRule(MOS6502Opcode.STX_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),

        // $zpN = mos6502.sty.zp $y — same shape as StaZp.
        [MOS6502Op.StyZp] = new EmitRule(MOS6502Opcode.STY_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),

        // $a = mos6502.txa $x — both operands implicit in the encoding.
        [MOS6502Op.Txa]   = new EmitRule(MOS6502Opcode.TXA,          EmitOperandKind.Implied,         null),

        // mos6502.rts implicit $a, implicit $x, … — all operands implicit.
        [MOS6502Op.Rts]   = new EmitRule(MOS6502Opcode.RTS,          EmitOperandKind.Implied,         null),
    };

    public static EmitRule Get(MOS6502Op op)
        => Table.TryGetValue(op, out var rule) ? rule
        : throw new KeyNotFoundException(
            $"MOS6502MachineCodeEmitTable: no emit rule for {op}. " +
            "If instruction selection or addressing-mode selection now produces this opcode, " +
            "add a rule here (see notes/mir-to-machinecode-plan.md §2.2 and §7).");
}
