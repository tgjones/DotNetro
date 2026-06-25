using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

public sealed class RegisterCoalescerTests
{
    private static readonly RegisterCoalescerPass Pass;

    static RegisterCoalescerTests()
    {
        // The MOS6502 target's constructor registers the core/arith/cf/pseudo
        // dialects (via MirBootstrap) plus the mos6502 dialect, which the
        // coalescer needs to introspect pseudo.copy operands and operand classes.
        var target = new MOS6502Target();
        Pass = new RegisterCoalescerPass(target.RegisterInfo);
    }

    private static MirFunction NewFunction(string name) => new(name, [], IRType.Void);

    private static MirInstruction Copy(MirBlock block, MirOperand def, MirOperand use) =>
        block.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), def, use);

    private static bool IsVregVregCopy(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy
        && instr.Operands is [VirtualReg { IsDefinition: true }, VirtualReg { IsDefinition: false }];

    // A vreg↔vreg copy whose two ends never overlap is coalesced away: the copy
    // is deleted and the victim's references are renamed to the survivor (the
    // lower-id vreg). This is the core join — the two-address-copy removal the
    // reference performs after TwoAddress.
    //
    //   %0 = pseudo.copy $a      %0 = pseudo.copy $a
    //   %1 = pseudo.copy %0   →  $x = pseudo.copy %0
    //   $x = pseudo.copy %1
    [Test]
    public async Task NonInterferingVregCopy_IsCoalesced()
    {
        var fn = NewFunction("coalesce");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        Copy(bb0, new VirtualReg(v0, IsDefinition: true),
                  new PhysicalReg(MOS6502Registers.A, IsDefinition: false));   // hint, kept
        Copy(bb0, new VirtualReg(v1, IsDefinition: true),
                  new VirtualReg(v0, IsDefinition: false));                     // coalesced
        var use = Copy(bb0, new PhysicalReg(MOS6502Registers.X, IsDefinition: true),
                            new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn);

        // The vreg↔vreg copy is gone; only the two physreg (hint) copies remain.
        await Assert.That(bb0.Instructions.Count).IsEqualTo(2);
        await Assert.That(bb0.Instructions.Any(IsVregVregCopy)).IsFalse();
        // The surviving use now reads the survivor (v0), not the renamed victim.
        await Assert.That(((VirtualReg)use.Operands[1]).Id).IsEqualTo(v0);
    }

    // When the copy's source stays live PAST the copy (it is read again after the
    // copy's destination is also live), the two ends overlap and must NOT be
    // coalesced — the copy is preserved. This is the interference guard that makes
    // two-address-copy removal safe.
    //
    //   %0 = pseudo.copy $a
    //   %1 = pseudo.copy %0      ; %0 and %1 both live across here...
    //   $x = pseudo.copy %0      ; ...because %0 is read again
    //   $y = pseudo.copy %1
    [Test]
    public async Task InterferingVregCopy_IsPreserved()
    {
        var fn = NewFunction("no_coalesce");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        Copy(bb0, new VirtualReg(v0, IsDefinition: true),
                  new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        Copy(bb0, new VirtualReg(v1, IsDefinition: true),
                  new VirtualReg(v0, IsDefinition: false));
        Copy(bb0, new PhysicalReg(MOS6502Registers.X, IsDefinition: true),
                  new VirtualReg(v0, IsDefinition: false));   // v0 read again → overlap
        Copy(bb0, new PhysicalReg(MOS6502Registers.Y, IsDefinition: true),
                  new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn);

        // All four copies remain; the vreg↔vreg one was not coalesced.
        await Assert.That(bb0.Instructions.Count).IsEqualTo(4);
        await Assert.That(bb0.Instructions.Count(IsVregVregCopy)).IsEqualTo(1);
    }

    // A copy touching a physical register is never coalesced — it is left in place
    // as an allocation hint (llvm's canJoinPhys only merges into RESERVED physregs;
    // ours are not). The pass leaves such a function untouched.
    [Test]
    public async Task PhysregCopy_IsLeftAsHint()
    {
        var fn = NewFunction("hint_only");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var copy = Copy(bb0, new VirtualReg(v0, IsDefinition: true),
                             new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        Copy(bb0, new PhysicalReg(MOS6502Registers.X, IsDefinition: true),
                  new VirtualReg(v0, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(bb0.Instructions.Count).IsEqualTo(2);
        await Assert.That(bb0.Instructions[0]).IsSameReferenceAs(copy);
    }
}
