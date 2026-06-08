using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// Tests for the post-RA RegisterScavengingPass (plan §3.6). The pass assigns a
// physical GPR to each copy-scratch vreg that pseudo expansion left behind (the
// 3-operand "scratch form" pseudo.copy), preferring a GPR that is DEAD at the
// copy's point, then drives the target's final lowering.
//
// We build the scratch form directly (as MOS6502PseudoExpander would emit it for
// an immediate→zero-page copy) and assert which GPR the scavenger picks by
// reading the load opcode it lowers to (LdaImm⇒$a, LdxImm⇒$x, LdyImm⇒$y).
public sealed class RegisterScavengingTests
{
    private static readonly RegisterScavengingPass Pass;

    static RegisterScavengingTests()
    {
        var target = new MOS6502Target();
        Pass = new RegisterScavengingPass(target.RegisterInfo, target.PseudoExpander);
    }

    private static MirFunction NewFunction(string name) => new(name, [], IRType.Void);

    // Build an immediate→zp scratch-form copy: $zpDst = pseudo.copy <imm> using
    // a fresh scratch vreg (third operand). Returns the instruction.
    private static MirInstruction AddScratchCopy(MirFunction fn, MirBlock block, int zpDst, long imm)
    {
        var scratch = fn.CreateVirtualRegister(IRType.I8);
        return block.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(zpDst, IsDefinition: true),
            new Immediate(imm),
            new VirtualReg(scratch, IsDefinition: true));
    }

    // The opcode of the (first) load the scratch form lowered to.
    private static MOS6502Op LoweredLoadOp(MirBlock block, int instrIndex) =>
        (MOS6502Op)block.Instructions[instrIndex].Opcode.Code;

    // -------------------------------------------------------------------------
    // 1. No surrounding pressure: the scavenger picks the first candidate ($a).
    //    The lowered form is `LDA #imm ; STA $zp`.
    // -------------------------------------------------------------------------
    [Test]
    public async Task NoPressure_PicksFirstCandidateA()
    {
        var fn = NewFunction("scratch_free");
        var bb0 = fn.CreateBlock();
        AddScratchCopy(fn, bb0, MOS6502Registers.RC(2), 0x12);

        Pass.Run(fn);

        // Lowered to LDA #$12 ; STA $zp2 — scratch is $a (first free GPR).
        await Assert.That(LoweredLoadOp(bb0, 0)).IsEqualTo(MOS6502Op.LdaImm);
        await Assert.That(LoweredLoadOp(bb0, 1)).IsEqualTo(MOS6502Op.StaZp);
        await Assert.That(bb0.Instructions.OfType<MirInstruction>()
            .SelectMany(i => i.Operands).OfType<VirtualReg>().Any()).IsFalse();
    }

    // -------------------------------------------------------------------------
    // 2. $a is live across the copy (a value defined before and used after):
    //    the scavenger must avoid $a and pick the next dead GPR, $x.
    //
    //   $a = mos6502.ldx.imm? ... (we need $a defined-then-used around the copy)
    // We model $a's liveness with two explicit-physreg instructions that the
    // scratch copy sits between: a def of $a before, a use of $a after.
    // -------------------------------------------------------------------------
    [Test]
    public async Task ALiveAcrossCopy_PicksX()
    {
        var fn = NewFunction("scratch_a_live");
        var bb0 = fn.CreateBlock();

        // $a := #0  (defines $a)
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaImm),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
            new Immediate(0));
        // scratch-form copy in the middle.
        AddScratchCopy(fn, bb0, MOS6502Registers.RC(2), 0x34);
        // $zp3 = sta.zp $a  (uses $a — so $a is live ACROSS the scratch copy)
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StaZp),
            new PhysicalReg(MOS6502Registers.RC(3), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        // The scratch copy (now at index 1) lowered to LDX #$34 ; STX $zp2 —
        // $a is busy, so the scavenger picked $x.
        await Assert.That(LoweredLoadOp(bb0, 1)).IsEqualTo(MOS6502Op.LdxImm);
        await Assert.That(LoweredLoadOp(bb0, 2)).IsEqualTo(MOS6502Op.StxZp);
    }

    // -------------------------------------------------------------------------
    // 3. $a and $x are both live across the copy: the scavenger falls to $y.
    // -------------------------------------------------------------------------
    [Test]
    public async Task AAndXLiveAcrossCopy_PicksY()
    {
        var fn = NewFunction("scratch_ax_live");
        var bb0 = fn.CreateBlock();

        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaImm),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true), new Immediate(0));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdxImm),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: true), new Immediate(0));
        AddScratchCopy(fn, bb0, MOS6502Registers.RC(2), 0x56);
        // Use both $a and $x after, holding them live across the copy.
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StaZp),
            new PhysicalReg(MOS6502Registers.RC(3), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StxZp),
            new PhysicalReg(MOS6502Registers.RC(4), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: false));

        Pass.Run(fn);

        // Scratch copy at index 2 lowered to LDY #$56 ; STY $zp2.
        await Assert.That(LoweredLoadOp(bb0, 2)).IsEqualTo(MOS6502Op.LdyImm);
        await Assert.That(LoweredLoadOp(bb0, 3)).IsEqualTo(MOS6502Op.StyZp);
    }

    // -------------------------------------------------------------------------
    // 4. All three GPRs live across the copy → no scratch is free → the
    //    emergency save/restore path (Phase 4) is reached and throws clearly.
    // -------------------------------------------------------------------------
    [Test]
    public async Task AllGprsLive_ThrowsNotImplemented()
    {
        var fn = NewFunction("scratch_none_free");
        var bb0 = fn.CreateBlock();

        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaImm),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true), new Immediate(0));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdxImm),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: true), new Immediate(0));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdyImm),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: true), new Immediate(0));
        AddScratchCopy(fn, bb0, MOS6502Registers.RC(2), 0x78);
        // Hold all three GPRs live across the scratch copy.
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StaZp),
            new PhysicalReg(MOS6502Registers.RC(3), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StxZp),
            new PhysicalReg(MOS6502Registers.RC(4), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: false));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StyZp),
            new PhysicalReg(MOS6502Registers.RC(5), IsDefinition: true),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<NotImplementedException>();
    }

    // -------------------------------------------------------------------------
    // 5. A function with no scratch-form copies is left untouched (fast path).
    // -------------------------------------------------------------------------
    [Test]
    public async Task NoScratchCopies_NoOp()
    {
        var fn = NewFunction("no_scratch");
        var bb0 = fn.CreateBlock();
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaImm),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true), new Immediate(1));

        Pass.Run(fn);

        await Assert.That(bb0.Instructions.Count).IsEqualTo(1);
        await Assert.That(LoweredLoadOp(bb0, 0)).IsEqualTo(MOS6502Op.LdaImm);
    }
}
