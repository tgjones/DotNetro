using Irie.Dialects.Arith;
using Irie.Dialects.Mem;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

public sealed class GenericMirVerifierTests
{
    private static readonly GenericMirVerifierPass Pass = new();

    static GenericMirVerifierTests()
    {
        // Constructing the MOS6502 target registers the core/arith/cf/pseudo/mem
        // dialects (via MirBootstrap), which the verifier looks up by ID.
        _ = new MOS6502Target();
    }

    private static MirFunction NewFunction(string name) =>
        new(name, [], IRType.Void);

    // arith.addi with an inline value literal as its second operand violates the
    // invariant — the literal must be an arith.constant def, not an inline
    // Immediate — so the verifier throws.
    [Test]
    public async Task InlineValueImmediateInAddI_Throws()
    {
        var fn = NewFunction("inline_addi");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        // %1 = arith.addi %0, 1   (the `1` is an illegal inline value literal)
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new Immediate(1));

        await Assert.That(() => Pass.Run(fn)).Throws<MirVerificationException>();
    }

    // The cmpi predicate immediate, the mem.fill count immediate, and
    // arith.constant's own value immediate are all legal structural / constant
    // attributes — the verifier accepts them.
    [Test]
    public async Task LegalStructuralImmediates_DoNotThrow()
    {
        var fn = NewFunction("legal_immediates");
        var bb0 = fn.CreateBlock();

        var c = fn.CreateVirtualRegister(IRType.I8);
        var a = fn.CreateVirtualRegister(IRType.I8);
        var b = fn.CreateVirtualRegister(IRType.I8);
        var cmp = fn.CreateVirtualRegister(IRType.I1);
        var addr = fn.CreateVirtualRegister(IRType.I16);

        // %c = arith.constant 5        (value immediate at use 0 — legal)
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(c, IsDefinition: true),
            new Immediate(5));

        // %cmp = arith.cmpi slt, %a, %b   (predicate immediate at use 0 — legal)
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.CmpI),
            new VirtualReg(cmp, IsDefinition: true),
            new Immediate((long)ArithCmpPredicate.Slt),
            new VirtualReg(a, IsDefinition: false),
            new VirtualReg(b, IsDefinition: false));

        // mem.fill %addr, %c, 4        (count immediate at use 2 — legal)
        bb0.AddInstruction(MemDialect.OpRef(MemOp.Fill),
            new VirtualReg(addr, IsDefinition: false),
            new VirtualReg(c, IsDefinition: false),
            new Immediate(4));

        await Assert.That(() => Pass.Run(fn)).ThrowsNothing();
    }
}
