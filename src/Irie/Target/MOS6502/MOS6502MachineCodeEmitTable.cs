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

    // 1-byte immediate carrying the low (`SymbolLowByte`) or high
    // (`SymbolHighByte`) half of a Symbol operand's final address. The MIR
    // operand is a Symbol; the emitter produces a MachineCodeOperand.ExternalRef
    // tagged with SymbolHalf.LowByte / .HighByte so the assembler resolves only
    // that half.
    SymbolLowByte,
    SymbolHighByte,
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

        // $a, $c = mos6502.adc.imm $a, #N, $c — same operand block as AdcZp
        // (AdcInfo): def[0]=$a, def[1]=$c, use[0]=$a (tied), use[1]=imm, use[2]=$c.
        // Index 3 = use[1], the immediate. Used by hand-written runtime helpers
        // (e.g. WriteLineInt32's two's-complement negate).
        [MOS6502Op.AdcImm] = new EmitRule(MOS6502Opcode.ADC_Immediate, EmitOperandKind.Immediate, 3),

        // $a = mos6502.ora.imm $a, #N — def[0]=$a, use[0]=$a, use[1]=imm.
        // Index 2 = use[1], the immediate. Used to OR a digit value with '0'.
        [MOS6502Op.OraImm] = new EmitRule(MOS6502Opcode.ORA_Immediate, EmitOperandKind.Immediate, 2),

        // $a = mos6502.eor.imm $a, #N — same shape as OraImm. Used by the
        // two's-complement negate (EOR #$FF) in WriteLineInt32.
        [MOS6502Op.EorImm] = new EmitRule(MOS6502Opcode.EOR_Immediate, EmitOperandKind.Immediate, 2),

        // mos6502.inx / mos6502.iny / mos6502.dex / mos6502.dey — implied; both
        // operands (the X/Y read+write) are implicit. Emitted by the
        // increment strength-reduction peephole for in-place register ±1.
        [MOS6502Op.Inx]    = new EmitRule(MOS6502Opcode.INX,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Iny]    = new EmitRule(MOS6502Opcode.INY,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Dex]    = new EmitRule(MOS6502Opcode.DEX,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Dey]    = new EmitRule(MOS6502Opcode.DEY,          EmitOperandKind.Implied,         null),

        // $zpN = mos6502.inc.zp $zpN / mos6502.dec.zp $zpN — read-modify-write
        // in place. Operands: def[0]=zp, use[0]=zp. Index 0 = the zero-page
        // address. Emitted by the increment strength-reduction peephole.
        [MOS6502Op.IncZp]  = new EmitRule(MOS6502Opcode.INC_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),
        [MOS6502Op.DecZp]  = new EmitRule(MOS6502Opcode.DEC_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),

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

        // mos6502.jmp.ind <imm> — operands[0] = Immediate (zero-page address).
        // AddressingMode.Indirect (from MOS6502InstructionInfo) routes encoding
        // through EncodeTwoByteOperand, which accepts Immediate and writes
        // it as a 2-byte LE word (e.g. `JMP ($001E)` for `mos6502.jmp.ind 30`).
        [MOS6502Op.JmpInd] = new EmitRule(MOS6502Opcode.JMP_Indirect, EmitOperandKind.AbsoluteAddress, 0),

        // mos6502.jsr.abs @callee — operands[0] = Symbol; rest are implicit.
        [MOS6502Op.JsrAbs] = new EmitRule(MOS6502Opcode.JSR_Absolute, EmitOperandKind.AbsoluteAddress, 0),

        // Absolute-addressed loads/stores of a statically-known symbol. The
        // Symbol(name, offset) operand encodes as a 2-byte ExternalRef(Full).
        //
        // $a = mos6502.lda.abs @sym — def[0]=$a, use[0]=Symbol. Index 1 = use[0].
        [MOS6502Op.LdaAbs] = new EmitRule(MOS6502Opcode.LDA_Absolute, EmitOperandKind.AbsoluteAddress, 1),
        // $x = mos6502.ldx.abs @sym / $y = mos6502.ldy.abs @sym — same shape.
        [MOS6502Op.LdxAbs] = new EmitRule(MOS6502Opcode.LDX_Absolute, EmitOperandKind.AbsoluteAddress, 1),
        [MOS6502Op.LdyAbs] = new EmitRule(MOS6502Opcode.LDY_Absolute, EmitOperandKind.AbsoluteAddress, 1),
        // mos6502.sta.abs %val, @sym — use[0]=value (in $a, implicit in opcode),
        // use[1]=Symbol. Index 1 = use[1], the absolute address.
        [MOS6502Op.StaAbs] = new EmitRule(MOS6502Opcode.STA_Absolute, EmitOperandKind.AbsoluteAddress, 1),
        [MOS6502Op.StxAbs] = new EmitRule(MOS6502Opcode.STX_Absolute, EmitOperandKind.AbsoluteAddress, 1),
        [MOS6502Op.StyAbs] = new EmitRule(MOS6502Opcode.STY_Absolute, EmitOperandKind.AbsoluteAddress, 1),

        // $zpN = mos6502.sta.zp $a
        // Operands: def[0]=zp (the address), use[0]=$a.
        // Index 0 = def[0]; the source register is implicit in the opcode.
        [MOS6502Op.StaZp] = new EmitRule(MOS6502Opcode.STA_ZeroPage, EmitOperandKind.ZeroPageAddress, 0),

        // $a = mos6502.lda.imm #N
        // Operands: def[0]=$a, use[0]=Immediate. Index 1 = use[0].
        [MOS6502Op.LdaImm] = new EmitRule(MOS6502Opcode.LDA_Immediate, EmitOperandKind.Immediate, 1),

        // $x = mos6502.ldx.imm #N — same shape as LdaImm.
        [MOS6502Op.LdxImm] = new EmitRule(MOS6502Opcode.LDX_Immediate, EmitOperandKind.Immediate, 1),

        // $x = mos6502.ldx.imm.symlo @sym — the $x counterpart of LdaImmSymLo.
        // Encodes as LDX_Immediate with the low byte of @sym. (High byte below.)
        [MOS6502Op.LdxImmSymLo] = new EmitRule(MOS6502Opcode.LDX_Immediate, EmitOperandKind.SymbolLowByte, 1),
        [MOS6502Op.LdxImmSymHi] = new EmitRule(MOS6502Opcode.LDX_Immediate, EmitOperandKind.SymbolHighByte, 1),

        // $y = mos6502.ldy.imm #N — same shape as LdaImm.
        [MOS6502Op.LdyImm] = new EmitRule(MOS6502Opcode.LDY_Immediate, EmitOperandKind.Immediate, 1),

        // $a = mos6502.lda.imm.symlo @sym — operands: def[0]=$a, use[0]=Symbol.
        // Encodes as LDA_Immediate with the low byte of @sym.
        [MOS6502Op.LdaImmSymLo] = new EmitRule(MOS6502Opcode.LDA_Immediate, EmitOperandKind.SymbolLowByte, 1),

        // $a = mos6502.lda.imm.symhi @sym — same shape, high byte.
        [MOS6502Op.LdaImmSymHi] = new EmitRule(MOS6502Opcode.LDA_Immediate, EmitOperandKind.SymbolHighByte, 1),

        // $a = mos6502.lda.indy $zpN
        // Operands: def[0]=$a, use[0]=zp (pointer low byte). Implicit-use $y
        // (the Y register for the offset) follows.
        // Index 1 = use[0].
        [MOS6502Op.LdaIndY] = new EmitRule(MOS6502Opcode.LDA_IndirectY, EmitOperandKind.ZeroPageAddress, 1),

        // mos6502.sta.indy $zpN, $a
        // Operands: use[0]=zp (pointer low byte), use[1]=$a (source). Implicit-use $y.
        // Index 0 = use[0]; the source is in $a per the opcode definition.
        [MOS6502Op.StaIndY] = new EmitRule(MOS6502Opcode.STA_IndirectY, EmitOperandKind.ZeroPageAddress, 0),

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

        // Register-transfer ops — both operands implicit in the encoding. The
        // register allocator's coalescer now routes copies through whichever
        // architectural register is free, so all four GPR transfers (not just
        // TXA) can reach the emitter; each is a single implied-mode byte.
        [MOS6502Op.Txa]   = new EmitRule(MOS6502Opcode.TXA,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Tax]   = new EmitRule(MOS6502Opcode.TAX,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Tay]   = new EmitRule(MOS6502Opcode.TAY,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Tya]   = new EmitRule(MOS6502Opcode.TYA,          EmitOperandKind.Implied,         null),

        // mos6502.rts implicit $a, implicit $x, … — all operands implicit.
        [MOS6502Op.Rts]   = new EmitRule(MOS6502Opcode.RTS,          EmitOperandKind.Implied,         null),

        // mos6502.pha pushes $a / mos6502.pla pulls $a — both implied; the $a
        // operand is implicit in the encoding.
        [MOS6502Op.Pha]   = new EmitRule(MOS6502Opcode.PHA,          EmitOperandKind.Implied,         null),
        [MOS6502Op.Pla]   = new EmitRule(MOS6502Opcode.PLA,          EmitOperandKind.Implied,         null),
    };

    public static EmitRule Get(MOS6502Op op)
        => Table.TryGetValue(op, out var rule) ? rule
        : throw new KeyNotFoundException(
            $"MOS6502MachineCodeEmitTable: no emit rule for {op}. " +
            "If instruction selection or addressing-mode selection now produces this opcode, " +
            "add a rule here (see notes/mir-to-machinecode-plan.md §2.2 and §7).");
}
