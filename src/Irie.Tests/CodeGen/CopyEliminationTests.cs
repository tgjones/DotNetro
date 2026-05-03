using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.CodeGen;

public sealed class CopyEliminationTests
{
    private static readonly CopyEliminationPass Pass = new();

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    // $A = GenericCopy $A  →  removed.
    [Test]
    public async Task IdentityCopy_IsRemoved()
    {
        var fn = new MachineFunction("identity");
        var bb0 = fn.CreateBasicBlock();

        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(bb0.Instructions.Count).IsEqualTo(0);
    }

    // $A = GenericCopy $X  →  kept (different registers).
    [Test]
    public async Task NonIdentityCopy_IsKept()
    {
        var fn = new MachineFunction("non_identity");
        var bb0 = fn.CreateBasicBlock();

        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.X, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(bb0.Instructions.Count).IsEqualTo(1);
    }

    // Only identity copies are removed; non-identity copies and other instructions
    // are left untouched.
    //
    //   $A = GenericCopy $A    ← identity, removed
    //   $A = GenericCopy $X    ← non-identity, kept
    //   ADC_ZeroPage ...       ← not a copy, kept
    //
    [Test]
    public async Task MixedInstructions_OnlyIdentityCopiesRemoved()
    {
        var fn = new MachineFunction("mixed");
        var bb0 = fn.CreateBasicBlock();

        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        var iNonIdentity = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.X, IsDefinition: false));
        var iAdc = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.C, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false),
            new PhysicalRegisterOperand(MOS6502Registers.RC(4), IsDefinition: false),
            new PhysicalRegisterOperand(MOS6502Registers.C, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(bb0.Instructions.Count).IsEqualTo(2);
        await Assert.That(bb0.Instructions[0]).IsEqualTo(iNonIdentity);
        await Assert.That(bb0.Instructions[1]).IsEqualTo(iAdc);
    }

    // End-to-end: RegisterAllocatorPass followed by CopyEliminationPass on the
    // IntegerAdd32 post-isel function.
    //
    // After RA all argument/return copies become identity copies ($A = GenericCopy $A,
    // $X = GenericCopy $X, etc.).  CopyElim removes all eight of them, leaving only:
    //   LDImm1, ADC×4, RTS  (6 instructions total).
    //
    [Test]
    public async Task IntegerAdd32_AfterRaThenCopyElim_IdentityCopiesGone()
    {
        var fn = new MachineFunction("IntegerAdd32");
        var bb0 = fn.CreateBasicBlock();

        bb0.LiveIns.AddRange([
            MOS6502Registers.A, MOS6502Registers.X,
            MOS6502Registers.RC(2), MOS6502Registers.RC(3),
            MOS6502Registers.RC(4), MOS6502Registers.RC(5),
            MOS6502Registers.RC(6), MOS6502Registers.RC(7),
        ]);

        var v0  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v2  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v3  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v5  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);
        var v6  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);
        var v7  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);
        var v8  = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);
        var v32 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Cc);
        var v33 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v34 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Cc);
        var v35 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v36 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Cc);
        var v37 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v38 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Cc);
        var v39 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v40 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Cc);

        static PhysicalRegisterOperand P(int r, bool def = false, bool isImplicit = false) =>
            new(r, def, isImplicit);
        static VirtualRegisterOperand V(int v, bool def = false) =>
            new(v, def);

        // 8 identity-after-RA argument copies.
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v0, def: true), P(MOS6502Registers.A));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v1, def: true), P(MOS6502Registers.X));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v2, def: true), P(MOS6502Registers.RC(2)));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v3, def: true), P(MOS6502Registers.RC(3)));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v5, def: true), P(MOS6502Registers.RC(4)));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v6, def: true), P(MOS6502Registers.RC(5)));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v7, def: true), P(MOS6502Registers.RC(6)));
        bb0.AddInstruction(GenericOpcode.GenericCopy, V(v8, def: true), P(MOS6502Registers.RC(7)));

        var iLDImm1 = bb0.AddInstruction(MOS6502Opcode.LDImm1, V(v32, def: true), new ImmediateOperand(0));

        var iAdc0 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            V(v33, def: true), V(v34, def: true), V(v0), V(v5), V(v32));
        var iAdc1 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            V(v35, def: true), V(v36, def: true), V(v1), V(v6), V(v34));
        var iAdc2 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            V(v37, def: true), V(v38, def: true), V(v2), V(v7), V(v36));
        var iAdc3 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            V(v39, def: true), V(v40, def: true), V(v3), V(v8), V(v38));

        // 4 identity-after-RA return copies.
        bb0.AddInstruction(GenericOpcode.GenericCopy, P(MOS6502Registers.A, def: true), V(v33));
        bb0.AddInstruction(GenericOpcode.GenericCopy, P(MOS6502Registers.X, def: true), V(v35));
        bb0.AddInstruction(GenericOpcode.GenericCopy, P(MOS6502Registers.RC(2), def: true), V(v37));
        bb0.AddInstruction(GenericOpcode.GenericCopy, P(MOS6502Registers.RC(3), def: true), V(v39));

        var iRts = bb0.AddInstruction(MOS6502Opcode.RTS,
            P(MOS6502Registers.A, isImplicit: true),
            P(MOS6502Registers.X, isImplicit: true),
            P(MOS6502Registers.RC(2), isImplicit: true),
            P(MOS6502Registers.RC(3), isImplicit: true));

        new RegisterAllocatorPass(new MOS6502RegisterInfo()).Run(fn);
        Pass.Run(fn);

        // All 8 argument copies and 4 return copies were identity → removed.
        // Remaining: LDImm1, ADC×4, RTS = 6 instructions.
        await Assert.That(bb0.Instructions.Count).IsEqualTo(6);
        await Assert.That(bb0.Instructions[0]).IsEqualTo(iLDImm1);
        await Assert.That(bb0.Instructions[1]).IsEqualTo(iAdc0);
        await Assert.That(bb0.Instructions[2]).IsEqualTo(iAdc1);
        await Assert.That(bb0.Instructions[3]).IsEqualTo(iAdc2);
        await Assert.That(bb0.Instructions[4]).IsEqualTo(iAdc3);
        await Assert.That(bb0.Instructions[5]).IsEqualTo(iRts);
    }
}
