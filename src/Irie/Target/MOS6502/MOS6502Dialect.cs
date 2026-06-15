using Irie.Mir;

namespace Irie.Target.MOS6502;

// The `mos6502` MIR dialect. Owns the target opcode enum and its dialect-side
// metadata (names, terminator/side-effect bits). Registered via the target's
// constructor by DialectRegistry, like the other dialects.
//
// Per-opcode operand classes and tied/implicit operands live on the
// target-side TargetInstructionInfo (still under construction in later steps);
// GetInstructionInfo here returns DialectInstructionInfo.Empty for now.
public sealed class MOS6502Dialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "mos6502";

    public static OpcodeRef OpRef(MOS6502Op op) => new(Id, (ushort)op);

    private static readonly Dictionary<MOS6502Op, string> _names = new()
    {
        // Pre-AMS (addressing-mode-agnostic)
        [MOS6502Op.Lda] = "lda",
        [MOS6502Op.Sta] = "sta",
        [MOS6502Op.Ldx] = "ldx",
        [MOS6502Op.Stx] = "stx",
        [MOS6502Op.Ldy] = "ldy",
        [MOS6502Op.Sty] = "sty",
        [MOS6502Op.Adc] = "adc",
        [MOS6502Op.Sbc] = "sbc",
        [MOS6502Op.And] = "and",
        [MOS6502Op.Ora] = "ora",
        [MOS6502Op.Eor] = "eor",
        [MOS6502Op.Cmp] = "cmp",
        [MOS6502Op.Cpx] = "cpx",
        [MOS6502Op.Cpy] = "cpy",
        [MOS6502Op.Inc] = "inc",
        [MOS6502Op.Dec] = "dec",
        [MOS6502Op.Asl] = "asl",
        [MOS6502Op.Lsr] = "lsr",
        [MOS6502Op.Rol] = "rol",
        [MOS6502Op.Ror] = "ror",

        // LDA
        [MOS6502Op.LdaImm]       = "lda.imm",
        [MOS6502Op.LdaImmSymLo]  = "lda.imm.symlo",
        [MOS6502Op.LdaImmSymHi]  = "lda.imm.symhi",
        [MOS6502Op.LdaZp]        = "lda.zp",
        [MOS6502Op.LdaZpX]  = "lda.zpx",
        [MOS6502Op.LdaAbs]  = "lda.abs",
        [MOS6502Op.LdaAbsX] = "lda.absx",
        [MOS6502Op.LdaAbsY] = "lda.absy",
        [MOS6502Op.LdaIndX] = "lda.indx",
        [MOS6502Op.LdaIndY] = "lda.indy",

        // STA
        [MOS6502Op.StaZp]   = "sta.zp",
        [MOS6502Op.StaZpX]  = "sta.zpx",
        [MOS6502Op.StaAbs]  = "sta.abs",
        [MOS6502Op.StaAbsX] = "sta.absx",
        [MOS6502Op.StaAbsY] = "sta.absy",
        [MOS6502Op.StaIndX] = "sta.indx",
        [MOS6502Op.StaIndY] = "sta.indy",

        // LDX
        [MOS6502Op.LdxImm]  = "ldx.imm",
        [MOS6502Op.LdxZp]   = "ldx.zp",
        [MOS6502Op.LdxZpY]  = "ldx.zpy",
        [MOS6502Op.LdxAbs]  = "ldx.abs",
        [MOS6502Op.LdxAbsY] = "ldx.absy",

        // STX
        [MOS6502Op.StxZp]   = "stx.zp",
        [MOS6502Op.StxZpY]  = "stx.zpy",
        [MOS6502Op.StxAbs]  = "stx.abs",

        // LDY
        [MOS6502Op.LdyImm]  = "ldy.imm",
        [MOS6502Op.LdyZp]   = "ldy.zp",
        [MOS6502Op.LdyZpX]  = "ldy.zpx",
        [MOS6502Op.LdyAbs]  = "ldy.abs",
        [MOS6502Op.LdyAbsX] = "ldy.absx",

        // STY
        [MOS6502Op.StyZp]   = "sty.zp",
        [MOS6502Op.StyZpX]  = "sty.zpx",
        [MOS6502Op.StyAbs]  = "sty.abs",

        // ADC
        [MOS6502Op.AdcImm]  = "adc.imm",
        [MOS6502Op.AdcZp]   = "adc.zp",
        [MOS6502Op.AdcZpX]  = "adc.zpx",
        [MOS6502Op.AdcAbs]  = "adc.abs",
        [MOS6502Op.AdcAbsX] = "adc.absx",
        [MOS6502Op.AdcAbsY] = "adc.absy",
        [MOS6502Op.AdcIndX] = "adc.indx",
        [MOS6502Op.AdcIndY] = "adc.indy",

        // SBC
        [MOS6502Op.SbcImm]  = "sbc.imm",
        [MOS6502Op.SbcZp]   = "sbc.zp",
        [MOS6502Op.SbcZpX]  = "sbc.zpx",
        [MOS6502Op.SbcAbs]  = "sbc.abs",
        [MOS6502Op.SbcAbsX] = "sbc.absx",
        [MOS6502Op.SbcAbsY] = "sbc.absy",
        [MOS6502Op.SbcIndX] = "sbc.indx",
        [MOS6502Op.SbcIndY] = "sbc.indy",

        // AND
        [MOS6502Op.AndImm]  = "and.imm",
        [MOS6502Op.AndZp]   = "and.zp",
        [MOS6502Op.AndZpX]  = "and.zpx",
        [MOS6502Op.AndAbs]  = "and.abs",
        [MOS6502Op.AndAbsX] = "and.absx",
        [MOS6502Op.AndAbsY] = "and.absy",
        [MOS6502Op.AndIndX] = "and.indx",
        [MOS6502Op.AndIndY] = "and.indy",

        // ORA
        [MOS6502Op.OraImm]  = "ora.imm",
        [MOS6502Op.OraZp]   = "ora.zp",
        [MOS6502Op.OraZpX]  = "ora.zpx",
        [MOS6502Op.OraAbs]  = "ora.abs",
        [MOS6502Op.OraAbsX] = "ora.absx",
        [MOS6502Op.OraAbsY] = "ora.absy",
        [MOS6502Op.OraIndX] = "ora.indx",
        [MOS6502Op.OraIndY] = "ora.indy",

        // EOR
        [MOS6502Op.EorImm]  = "eor.imm",
        [MOS6502Op.EorZp]   = "eor.zp",
        [MOS6502Op.EorZpX]  = "eor.zpx",
        [MOS6502Op.EorAbs]  = "eor.abs",
        [MOS6502Op.EorAbsX] = "eor.absx",
        [MOS6502Op.EorAbsY] = "eor.absy",
        [MOS6502Op.EorIndX] = "eor.indx",
        [MOS6502Op.EorIndY] = "eor.indy",

        // CMP
        [MOS6502Op.CmpImm]  = "cmp.imm",
        [MOS6502Op.CmpZp]   = "cmp.zp",
        [MOS6502Op.CmpZpX]  = "cmp.zpx",
        [MOS6502Op.CmpAbs]  = "cmp.abs",
        [MOS6502Op.CmpAbsX] = "cmp.absx",
        [MOS6502Op.CmpAbsY] = "cmp.absy",
        [MOS6502Op.CmpIndX] = "cmp.indx",
        [MOS6502Op.CmpIndY] = "cmp.indy",

        // CPX
        [MOS6502Op.CpxImm] = "cpx.imm",
        [MOS6502Op.CpxZp]  = "cpx.zp",
        [MOS6502Op.CpxAbs] = "cpx.abs",

        // CPY
        [MOS6502Op.CpyImm] = "cpy.imm",
        [MOS6502Op.CpyZp]  = "cpy.zp",
        [MOS6502Op.CpyAbs] = "cpy.abs",

        // INC / DEC (memory)
        [MOS6502Op.IncZp]   = "inc.zp",
        [MOS6502Op.IncZpX]  = "inc.zpx",
        [MOS6502Op.IncAbs]  = "inc.abs",
        [MOS6502Op.IncAbsX] = "inc.absx",
        [MOS6502Op.DecZp]   = "dec.zp",
        [MOS6502Op.DecZpX]  = "dec.zpx",
        [MOS6502Op.DecAbs]  = "dec.abs",
        [MOS6502Op.DecAbsX] = "dec.absx",

        // Register inc/dec
        [MOS6502Op.Inx] = "inx",
        [MOS6502Op.Dex] = "dex",
        [MOS6502Op.Iny] = "iny",
        [MOS6502Op.Dey] = "dey",

        // ASL
        [MOS6502Op.AslAcc]  = "asl.acc",
        [MOS6502Op.AslZp]   = "asl.zp",
        [MOS6502Op.AslZpX]  = "asl.zpx",
        [MOS6502Op.AslAbs]  = "asl.abs",
        [MOS6502Op.AslAbsX] = "asl.absx",

        // LSR
        [MOS6502Op.LsrAcc]  = "lsr.acc",
        [MOS6502Op.LsrZp]   = "lsr.zp",
        [MOS6502Op.LsrZpX]  = "lsr.zpx",
        [MOS6502Op.LsrAbs]  = "lsr.abs",
        [MOS6502Op.LsrAbsX] = "lsr.absx",

        // ROL
        [MOS6502Op.RolAcc]  = "rol.acc",
        [MOS6502Op.RolZp]   = "rol.zp",
        [MOS6502Op.RolZpX]  = "rol.zpx",
        [MOS6502Op.RolAbs]  = "rol.abs",
        [MOS6502Op.RolAbsX] = "rol.absx",

        // ROR
        [MOS6502Op.RorAcc]  = "ror.acc",
        [MOS6502Op.RorZp]   = "ror.zp",
        [MOS6502Op.RorZpX]  = "ror.zpx",
        [MOS6502Op.RorAbs]  = "ror.abs",
        [MOS6502Op.RorAbsX] = "ror.absx",

        // Branches
        [MOS6502Op.Beq] = "beq",
        [MOS6502Op.Bne] = "bne",
        [MOS6502Op.Bcc] = "bcc",
        [MOS6502Op.Bcs] = "bcs",
        [MOS6502Op.Bmi] = "bmi",
        [MOS6502Op.Bpl] = "bpl",
        [MOS6502Op.Bvc] = "bvc",
        [MOS6502Op.Bvs] = "bvs",
        [MOS6502Op.Bgt] = "bgt",

        // Abstract frame accesses (addressing-mode-agnostic; expanded post-RA).
        [MOS6502Op.FrameLoadByte]  = "frame.load.byte",
        [MOS6502Op.FrameStoreByte] = "frame.store.byte",

        // Jumps / calls / returns
        [MOS6502Op.JmpAbs] = "jmp.abs",
        [MOS6502Op.JmpInd] = "jmp.ind",
        [MOS6502Op.JsrAbs] = "jsr.abs",
        [MOS6502Op.Rts]    = "rts",
        [MOS6502Op.Rti]    = "rti",

        // Stack
        [MOS6502Op.Pha] = "pha",
        [MOS6502Op.Pla] = "pla",
        [MOS6502Op.Php] = "php",
        [MOS6502Op.Plp] = "plp",

        // Register transfers
        [MOS6502Op.Tax] = "tax",
        [MOS6502Op.Txa] = "txa",
        [MOS6502Op.Tay] = "tay",
        [MOS6502Op.Tya] = "tya",
        [MOS6502Op.Tsx] = "tsx",
        [MOS6502Op.Txs] = "txs",

        // Flag operations
        [MOS6502Op.Clc] = "clc",
        [MOS6502Op.Sec] = "sec",
        [MOS6502Op.Cli] = "cli",
        [MOS6502Op.Sei] = "sei",
        [MOS6502Op.Clv] = "clv",
        [MOS6502Op.Cld] = "cld",
        [MOS6502Op.Sed] = "sed",

        // Misc
        [MOS6502Op.Nop] = "nop",
        [MOS6502Op.Brk] = "brk",
    };

    private static readonly Dictionary<string, MOS6502Op> _byName =
        _names.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    private static readonly HashSet<MOS6502Op> _terminators =
    [
        // RTS / RTI: function returns. JMP: unconditional transfer, no
        // fallthrough. Conditional branches (Beq/Bne/etc.) are NOT terminators
        // in our model — they may branch to their target, but control falls
        // through to the next instruction on the not-taken path. JSR returns
        // to the caller after the called function returns, so it's not a
        // terminator either.
        MOS6502Op.Rts, MOS6502Op.Rti,
        MOS6502Op.JmpAbs, MOS6502Op.JmpInd,
    ];

    public override string GetOpName(ushort code)
        => _names.TryGetValue((MOS6502Op)code, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown mos6502 opcode {code}.");

    public override bool TryParseOp(string name, out ushort code)
    {
        if (_byName.TryGetValue(name, out var op))
        {
            code = (ushort)op;
            return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        (MOS6502Op)code switch
        {
            MOS6502Op.Adc    => AdcInfo,
            MOS6502Op.AdcZp  => AdcInfo,
            MOS6502Op.AdcImm => AdcInfo,
            MOS6502Op.Sbc    => SbcInfo,
            MOS6502Op.SbcZp  => SbcInfo,
            MOS6502Op.SbcImm => SbcInfo,
            MOS6502Op.Cmp    => CmpInfo,
            MOS6502Op.CmpZp  => CmpInfo,
            MOS6502Op.CmpImm => CmpInfo,
            MOS6502Op.EorImm => EorImmInfo,
            MOS6502Op.FrameLoadByte  => FrameLoadByteInfo,
            MOS6502Op.FrameStoreByte => FrameStoreByteInfo,
            _ => DialectInstructionInfo.Empty,
        };

    // Abstract frame byte accesses. The shared scratch the absolute/indirect-Y
    // expansion uses is $y (the indexed offset) and the $rc0/$rc1 zero-page
    // pointer pair — declared here as implicit defs to document the clobber
    // contract. The instruction selector also materialises these as explicit
    // PhysicalReg(IsImplicit) operands (like mos6502.cmp's $n/$z/$c), which is
    // what RA actually reads to reserve them. No tied operands.
    //
    // The load defines its value in $a (Ac) — where lda.indy lands it. The store's
    // value is a use pinned to $x (Xc): the expansion clobbers $a (pointer LDAs)
    // and $y before the value reaches $a for sta.indy, so the value must live in
    // the one GPR the sequence never touches. The selector therefore adds $a as a
    // further explicit implicit-def on the store op (see MOS6502InstructionSelector).
    //
    //   %v : ac = frame.load.byte  @slot, #off   (def[0]=value)
    //             frame.store.byte  @slot, #off, %v : xc
    private static readonly DialectInstructionInfo FrameLoadByteInfo = new(
        ImplicitDefs: [MOS6502Registers.Y, MOS6502Registers.RC(0), MOS6502Registers.RC(1)]);

    private static readonly DialectInstructionInfo FrameStoreByteInfo = new(
        ImplicitDefs: [MOS6502Registers.Y, MOS6502Registers.RC(0), MOS6502Registers.RC(1)]);

    // Pre-AMS `mos6502.adc` and its AMS-refined variants (`adc.zp`, `adc.imm`).
    // 2 defs + 3 uses; use[0] is tied to def[0]. All addressing modes share
    // this operand block — the only thing AMS changes is the opcode tag.
    private static readonly DialectInstructionInfo AdcInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: carry_out
            MOS6502RegisterClass.Ac,    // use[0]: L (tied to def[0])
            MOS6502RegisterClass.Imag8, // use[1]: R
            MOS6502RegisterClass.Cc,    // use[2]: carry_in
        ],
        TiedOperands: [-1, -1, 0, -1, -1]);

    // Pre-AMS `mos6502.sbc` and its AMS-refined variants (`sbc.zp`, `sbc.imm`).
    // Same shape as Adc: 2 defs + 3 uses; use[0] tied to def[0]. The carry-in
    // here is the 6502 borrow-inverted carry flag (1 = no borrow).
    private static readonly DialectInstructionInfo SbcInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: borrow_out (carry-flag polarity)
            MOS6502RegisterClass.Ac,    // use[0]: L (tied to def[0])
            MOS6502RegisterClass.Imag8, // use[1]: R
            MOS6502RegisterClass.Cc,    // use[2]: borrow_in (carry-flag polarity)
        ],
        TiedOperands: [-1, -1, 0, -1, -1]);

    // Pre-AMS `mos6502.cmp` and its AMS-refined variants (`cmp.zp`, `cmp.imm`).
    // Explicit operands: use[0]=a (in $a), use[1]=b. Sets flags $n, $z, $c as
    // implicit defs — they live as separate PhysicalReg(IsImplicit=true)
    // entries in the operand array so RA understands the clobber. No tied
    // operands (cmp doesn't write back to A).
    private static readonly DialectInstructionInfo CmpInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // use[0]: a
            MOS6502RegisterClass.Imag8, // use[1]: b
        ],
        ImplicitDefs: [
            MOS6502Registers.N,
            MOS6502Registers.Z,
            MOS6502Registers.C,
        ]);

    // `mos6502.eor.imm` accumulator EOR: def[0]=$a (result), use[0]=$a (tied),
    // use[1]=immediate. The signed-compare sign-flip emits this with virtual
    // Ac vregs, so the tied-operand metadata is needed for the two-address pass
    // and RA to share a single $a across the def/use.
    private static readonly DialectInstructionInfo EorImmInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Ac,    // use[0]: a (tied to def[0])
        ],
        TiedOperands: [-1, 0]);

    // Target ops conservatively touch physregs/memory; treat them as having
    // side effects for DCE purposes until a finer-grained model is in place.
    public override bool IsSideEffectFree(ushort code) => false;

    public override bool IsTerminator(ushort code) => _terminators.Contains((MOS6502Op)code);

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
