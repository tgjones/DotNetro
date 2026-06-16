namespace Irie.Target.MOS6502;

// Opcodes in the `mos6502` MIR dialect. Two flavours coexist:
//
// - Pre-AMS (addressing-mode-agnostic): emitted by the instruction selector
//   from arith/cf ops. The MOS6502AddressingModeSelectorPass refines each one
//   to a post-AMS form based on its concrete operands. e.g. `Adc` → `AdcZp` /
//   `AdcImm` / `AdcAbs`.
// - Post-AMS: the mnemonic-plus-addressing-mode forms that have a direct 6502
//   byte mapping. Mirrors MOS6502Opcode 1:1.
//
// Plus a few synthetic ops (e.g. `Bgt`) that PseudoExpansionPass lowers later
// to combinations of real branches.
public enum MOS6502Op : ushort
{
    // ===== Pre-AMS (addressing-mode-agnostic) =====
    Lda,
    Sta,
    Ldx,
    Stx,
    Ldy,
    Sty,
    Adc,
    Sbc,
    And,
    Ora,
    Eor,
    Cmp,
    Cpx,
    Cpy,
    Inc,
    Dec,
    Asl,
    Lsr,
    Rol,
    Ror,

    // ===== Post-AMS =====

    // LDA
    LdaImm,
    // LDA immediate with the low or high byte of a Symbol address. Both encode
    // as the LDA_Immediate byte (0xA9); the operand is a Symbol that the
    // assembler resolves to the low (`<sym`) or high (`>sym`) half of the
    // symbol's final address. Used to materialize the bytes of a `mem.symbol`
    // result into zero page for indirect-Y addressing.
    LdaImmSymLo,
    LdaImmSymHi,
    LdaZp,
    LdaZpX,
    LdaAbs,
    LdaAbsX,
    LdaAbsY,
    LdaIndX,
    LdaIndY,

    // STA
    StaZp,
    StaZpX,
    StaAbs,
    StaAbsX,
    StaAbsY,
    StaIndX,
    StaIndY,

    // LDX
    LdxImm,
    // LDX immediate with the low or high byte of a Symbol address. Both encode
    // as the LDX_Immediate byte (0xA2); the operand is a Symbol resolved to the
    // low (`<sym`) or high (`>sym`) half of the symbol's address. The $x
    // counterpart of LdaImmSymLo/Hi — used by frame lowering to build a slot's
    // zero-page pointer pair via $x, so a store value already parked in $a is
    // preserved across the pointer setup.
    LdxImmSymLo,
    LdxImmSymHi,
    LdxZp,
    LdxZpY,
    LdxAbs,
    LdxAbsY,

    // STX
    StxZp,
    StxZpY,
    StxAbs,

    // LDY
    LdyImm,
    LdyZp,
    LdyZpX,
    LdyAbs,
    LdyAbsX,

    // STY
    StyZp,
    StyZpX,
    StyAbs,

    // ADC
    AdcImm,
    AdcZp,
    AdcZpX,
    AdcAbs,
    AdcAbsX,
    AdcAbsY,
    AdcIndX,
    AdcIndY,

    // SBC
    SbcImm,
    SbcZp,
    SbcZpX,
    SbcAbs,
    SbcAbsX,
    SbcAbsY,
    SbcIndX,
    SbcIndY,

    // AND
    AndImm,
    AndZp,
    AndZpX,
    AndAbs,
    AndAbsX,
    AndAbsY,
    AndIndX,
    AndIndY,

    // ORA
    OraImm,
    OraZp,
    OraZpX,
    OraAbs,
    OraAbsX,
    OraAbsY,
    OraIndX,
    OraIndY,

    // EOR
    EorImm,
    EorZp,
    EorZpX,
    EorAbs,
    EorAbsX,
    EorAbsY,
    EorIndX,
    EorIndY,

    // CMP
    CmpImm,
    CmpZp,
    CmpZpX,
    CmpAbs,
    CmpAbsX,
    CmpAbsY,
    CmpIndX,
    CmpIndY,

    // CPX
    CpxImm,
    CpxZp,
    CpxAbs,

    // CPY
    CpyImm,
    CpyZp,
    CpyAbs,

    // INC / DEC (memory)
    IncZp,
    IncZpX,
    IncAbs,
    IncAbsX,
    DecZp,
    DecZpX,
    DecAbs,
    DecAbsX,

    // Register inc/dec
    Inx,
    Dex,
    Iny,
    Dey,

    // ASL
    AslAcc,
    AslZp,
    AslZpX,
    AslAbs,
    AslAbsX,

    // LSR
    LsrAcc,
    LsrZp,
    LsrZpX,
    LsrAbs,
    LsrAbsX,

    // ROL
    RolAcc,
    RolZp,
    RolZpX,
    RolAbs,
    RolAbsX,

    // ROR
    RorAcc,
    RorZp,
    RorZpX,
    RorAbs,
    RorAbsX,

    // Branches
    Beq,
    Bne,
    Bcc,
    Bcs,
    Bmi,
    Bpl,
    Bvc,
    Bvs,

    // Synthetic branch — expanded later into real branches by
    // PseudoExpansionPass (or a target-internal peephole).
    Bgt,

    // ===== Abstract frame accesses (the llvm-mos LDStk/STStk analogues) =====
    //
    // Addressing-mode-agnostic byte load/store against a FrameSlot, carrying the
    // slot's symbol + a byte offset. Emitted at instruction selection for any
    // byte access whose address resolves to a frame slot; expanded post-RA by
    // FrameAccessLoweringPass → MOS6502FrameLowering.LowerFrameAccess into the
    // concrete sequence chosen from the slot's StackId (today: always absolute
    // indirect-Y; Stage 3 adds the direct-zp branch). The value byte lives in
    // $a and the scratch the lowering uses ($y, $rc0, $rc1) is declared as an
    // implicit clobber, so RA models both ends — no register scavenging later.
    FrameLoadByte,
    FrameStoreByte,

    // Jumps / calls / returns
    JmpAbs,
    JmpInd,
    JsrAbs,
    Rts,
    Rti,

    // Stack
    Pha,
    Pla,
    Php,
    Plp,

    // Register transfers
    Tax,
    Txa,
    Tay,
    Tya,
    Tsx,
    Txs,

    // Flag operations
    Clc,
    Sec,
    Cli,
    Sei,
    Clv,
    Cld,
    Sed,

    // Misc
    Nop,
    Brk,
}
