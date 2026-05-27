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
