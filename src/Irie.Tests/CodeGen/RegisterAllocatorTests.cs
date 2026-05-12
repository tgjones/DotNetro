using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.CodeGen;

public sealed class RegisterAllocatorTests
{
    private static readonly RegisterAllocatorPass Pass;

    static RegisterAllocatorTests()
    {
        Pass = new RegisterAllocatorPass(new MOS6502RegisterInfo(), MOS6502InstructionInfo.Instance, MOS6502RegisterClass.Anyi8);
        new PassManager().AddPass(Pass);
    }

    // Helper: first physreg def in instruction.
    private static int DefPhysreg(MachineInstruction instr) =>
        instr.Operands.OfType<PhysicalRegisterOperand>().First(p => p.IsDefinition).Register;

    // Helper: nth physreg def (0-based) in instruction.
    private static int NthDefPhysreg(MachineInstruction instr, int n) =>
        instr.Operands.OfType<PhysicalRegisterOperand>().Where(p => p.IsDefinition).ElementAt(n).Register;

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    // Rule 1: vreg defined by a GenericCopy from a physreg gets that physreg as its hint.
    //
    //   %0:Ac = GenericCopy $A    → hint $A → assigned $A
    //
    [Test]
    public async Task HintFromPhysregCopy_AssignsHintedRegister()
    {
        var fn = new MachineFunction("hint_rule1");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        var i0 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(DefPhysreg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(i0.Operands.OfType<VirtualRegisterOperand>().Any()).IsFalse();
    }

    // Copy hint overrides class constraint: Ac class only contains $A, but if the
    // hint points to $X the vreg is assigned $X (identity copy survives to CopyElim).
    //
    //   %0:Ac = GenericCopy $X    → hint $X → assigned $X (outside Ac class)
    //
    [Test]
    public async Task HintOverridesClassConstraint_AssignsOutOfClassRegister()
    {
        var fn = new MachineFunction("hint_override");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        var i0 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.X, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(DefPhysreg(i0)).IsEqualTo(MOS6502Registers.X);
    }

    // Rule 2: vreg used by a GenericCopy into a physreg gets that physreg as its hint.
    //
    //   %0:Cc = SomeOp           → no hint; Cc → $C
    //   $A = GenericCopy %0      → hint $A for %0, but %0 already assigned $C
    //                               → hint wins only if the hinted physreg is free
    //
    // Here we test the simpler case where the hint is applied:
    //   %0:Imag8 = SomeOp        → no hint; Imag8 → RC2 (first allocatable)
    //   $RC2 = GenericCopy %0    → hint $RC2; assigned $RC2 ✓
    //
    [Test]
    public async Task HintFromPhysregCopyDef_Rule2()
    {
        const int dummyOp = 999;
        var fn = new MachineFunction("hint_rule2");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);

        // %0 is defined first (no copy hint at def site), then used in a copy to $RC2.
        // TryGetCopyHint scans all instructions, so it finds the rule-2 match.
        var i0 = bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v0, IsDefinition: true));
        var i1 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(MOS6502Registers.RC(2), IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));

        Pass.Run(fn);

        // %0 should be assigned $RC2 via the rule-2 hint.
        await Assert.That(i0.Operands[0]).IsEqualTo(
            new PhysicalRegisterOperand(MOS6502Registers.RC(2), IsDefinition: true));
        // The copy becomes $RC2 = GenericCopy $RC2 (identity; removed later by CopyElim).
        await Assert.That(i1.Operands[1]).IsEqualTo(
            new PhysicalRegisterOperand(MOS6502Registers.RC(2), IsDefinition: false));
    }

    // Rule 3: when the source of a vreg-to-vreg copy is already assigned, the hint
    // propagates to the destination.
    //
    //   %0:Ac = GenericCopy $A   → rule 1: %0 → $A
    //   %1:Ac = GenericCopy %0   → rule 3: %0 assigned $A → hint $A → %1 → $A
    //
    // %0 expires before %1 starts (at the same slot), so $A is free.
    //
    [Test]
    public async Task VregToVregHint_PropagatesAssignment_Rule3()
    {
        var fn = new MachineFunction("hint_rule3");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        var i0 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v1, IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));

        Pass.Run(fn);

        // %0 → $A (rule 1); %1 → $A (rule 3; %0 expires at its endpoint ≤ 1).
        await Assert.That(DefPhysreg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysreg(i1)).IsEqualTo(MOS6502Registers.A);
    }

    // With no GenericCopy nearby, a vreg falls back to the first allocatable in
    // its class. For Imag8 that is RC2 (RC0/RC1 reserved as soft stack pointer).
    //
    [Test]
    public async Task NoHint_FallsBackToFirstAllocatableInClass()
    {
        const int dummyOp = 999;
        var fn = new MachineFunction("no_hint");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Imag8);

        var i0 = bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v0, IsDefinition: true));

        Pass.Run(fn);

        await Assert.That(DefPhysreg(i0)).IsEqualTo(MOS6502Registers.RC(2));
    }

    // Expiry rule: endpoint ≤ currentStart (not <).
    //
    //   %0:Ac = GenericCopy $A   ; slot 0  →  %0 range [0, 1]
    //   %1:Ac = SomeOp %0        ; slot 1  →  %0 use (end=1), %1 def (start=1)
    //
    // When allocating %1 (start=1): %0 has end=1 ≤ 1 → expired → $A freed.
    // %1 has no hint (SomeOp is not GenericCopy), falls back to Ac → $A.
    //
    // If < were used instead, $A would not expire and the pass would throw.
    //
    [Test]
    public async Task ExpiryAtSameSlot_FreesPhysregForNextInterval()
    {
        const int dummyOp = 999;
        var fn = new MachineFunction("expiry");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        var i0 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v1, IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));

        Pass.Run(fn);

        // Both get $A: %0 via hint, %1 via class fallback after %0 expires.
        await Assert.That(DefPhysreg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysreg(i1)).IsEqualTo(MOS6502Registers.A);
    }

    // Two overlapping vregs in a single-register class (Ac, only $A) with no
    // hints → the second interval cannot be allocated → NotImplementedException.
    //
    [Test]
    public async Task TwoOverlappingIntervalsInSingleRegClass_ThrowsNotImplemented()
    {
        const int dummyOp = 999;
        var fn = new MachineFunction("spill");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v0, IsDefinition: true));
        bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v1, IsDefinition: true));
        // Use both so their ranges overlap.
        bb0.AddInstruction(dummyOp,
            new VirtualRegisterOperand(v0, IsDefinition: false),
            new VirtualRegisterOperand(v1, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<NotImplementedException>();
    }

    // Full IntegerAdd32 allocation: the post-isel MIR shape produced by MOS6502
    // InstructionSelector for a 32-bit integer addition.  Verifies that copy
    // hints (rules 1 & 2) assign the correct physregs and that the ≤ expiry rule
    // lets $A / $X / $RC2 / $RC3 / $C be reused by the ADC result vregs.
    //
    // Expected assignments:
    //   %0:Ac → $A    (hint from GenericCopy $A)
    //   %1:Ac → $X    (hint from GenericCopy $X; overrides Ac class)
    //   %2:Ac → $RC2  (hint from GenericCopy $RC2; overrides Ac class)
    //   %3:Ac → $RC3  (hint; overrides Ac)
    //   %5:Imag8 → $RC4  (hint from GenericCopy $RC4)
    //   %6:Imag8 → $RC5
    //   %7:Imag8 → $RC6
    //   %8:Imag8 → $RC7
    //   %32:Cc → $C   (class fallback; Cc has only $C)
    //   %33:Ac → $A   (hint from $A = GenericCopy %33; %0 expired)
    //   %34:Cc → $C   (%32 expired at same slot 9)
    //   %35:Ac → $X   (hint; %1 expired)
    //   %36:Cc → $C
    //   %37:Ac → $RC2 (hint; %2 expired)
    //   %38:Cc → $C
    //   %39:Ac → $RC3 (hint; %3 expired)
    //   %40:Cc → $C
    //
    [Test]
    public async Task IntegerAdd32_FullAllocation_AssignsCorrectPhysregs()
    {
        var fn = new MachineFunction("IntegerAdd32");
        var bb0 = fn.CreateBasicBlock();

        // Live-in physregs (ABI).
        bb0.LiveIns.AddRange([
            MOS6502Registers.A, MOS6502Registers.X,
            MOS6502Registers.RC(2), MOS6502Registers.RC(3),
            MOS6502Registers.RC(4), MOS6502Registers.RC(5),
            MOS6502Registers.RC(6), MOS6502Registers.RC(7),
        ]);

        // Create vregs in definition order, matching InstructionSelector output.
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

        // Argument copies (liveins → vregs).
        var iCopyA   = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v0, def: true), PhysReg(MOS6502Registers.A));
        var iCopyX   = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v1, def: true), PhysReg(MOS6502Registers.X));
        var iCopyRC2 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v2, def: true), PhysReg(MOS6502Registers.RC(2)));
        var iCopyRC3 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v3, def: true), PhysReg(MOS6502Registers.RC(3)));
        var iCopyRC4 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v5, def: true), PhysReg(MOS6502Registers.RC(4)));
        var iCopyRC5 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v6, def: true), PhysReg(MOS6502Registers.RC(5)));
        var iCopyRC6 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v7, def: true), PhysReg(MOS6502Registers.RC(6)));
        var iCopyRC7 = bb0.AddInstruction(GenericOpcode.GenericCopy, VReg(v8, def: true), PhysReg(MOS6502Registers.RC(7)));

        // Carry-in materialisation.
        var iLDImm1 = bb0.AddInstruction(MOS6502Opcode.LDImm1,
            VReg(v32, def: true), new ImmediateOperand(0));

        // ADC chain.
        var iAdc0 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            VReg(v33, def: true), VReg(v34, def: true), VReg(v0), VReg(v5), VReg(v32));
        var iAdc1 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            VReg(v35, def: true), VReg(v36, def: true), VReg(v1), VReg(v6), VReg(v34));
        var iAdc2 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            VReg(v37, def: true), VReg(v38, def: true), VReg(v2), VReg(v7), VReg(v36));
        var iAdc3 = bb0.AddInstruction(MOS6502Opcode.ADC_ZeroPage,
            VReg(v39, def: true), VReg(v40, def: true), VReg(v3), VReg(v8), VReg(v38));

        // Return copies (vregs → ABI physregs).
        var iRetA   = bb0.AddInstruction(GenericOpcode.GenericCopy, PhysReg(MOS6502Registers.A, def: true), VReg(v33));
        var iRetX   = bb0.AddInstruction(GenericOpcode.GenericCopy, PhysReg(MOS6502Registers.X, def: true), VReg(v35));
        var iRetRC2 = bb0.AddInstruction(GenericOpcode.GenericCopy, PhysReg(MOS6502Registers.RC(2), def: true), VReg(v37));
        var iRetRC3 = bb0.AddInstruction(GenericOpcode.GenericCopy, PhysReg(MOS6502Registers.RC(3), def: true), VReg(v39));

        bb0.AddInstruction(MOS6502Opcode.RTS,
            PhysReg(MOS6502Registers.A, def: false, isImplicit: true),
            PhysReg(MOS6502Registers.X, def: false, isImplicit: true),
            PhysReg(MOS6502Registers.RC(2), def: false, isImplicit: true),
            PhysReg(MOS6502Registers.RC(3), def: false, isImplicit: true));

        Pass.Run(fn);

        // No virtual registers remain.
        foreach (var instr in bb0.Instructions)
            await Assert.That(instr.Operands.OfType<VirtualRegisterOperand>().Any()).IsFalse();

        // Argument copies become identity copies (hint = live-in physreg).
        await Assert.That(DefPhysreg(iCopyA)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysreg(iCopyX)).IsEqualTo(MOS6502Registers.X);
        await Assert.That(DefPhysreg(iCopyRC2)).IsEqualTo(MOS6502Registers.RC(2));
        await Assert.That(DefPhysreg(iCopyRC3)).IsEqualTo(MOS6502Registers.RC(3));
        await Assert.That(DefPhysreg(iCopyRC4)).IsEqualTo(MOS6502Registers.RC(4));
        await Assert.That(DefPhysreg(iCopyRC5)).IsEqualTo(MOS6502Registers.RC(5));
        await Assert.That(DefPhysreg(iCopyRC6)).IsEqualTo(MOS6502Registers.RC(6));
        await Assert.That(DefPhysreg(iCopyRC7)).IsEqualTo(MOS6502Registers.RC(7));

        // LDImm1 carry-in → $C.
        await Assert.That(DefPhysreg(iLDImm1)).IsEqualTo(MOS6502Registers.C);

        // ADC results: each pair reuses the input physreg after it expires.
        await Assert.That(NthDefPhysreg(iAdc0, 0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(NthDefPhysreg(iAdc0, 1)).IsEqualTo(MOS6502Registers.C);
        await Assert.That(NthDefPhysreg(iAdc1, 0)).IsEqualTo(MOS6502Registers.X);
        await Assert.That(NthDefPhysreg(iAdc1, 1)).IsEqualTo(MOS6502Registers.C);
        await Assert.That(NthDefPhysreg(iAdc2, 0)).IsEqualTo(MOS6502Registers.RC(2));
        await Assert.That(NthDefPhysreg(iAdc2, 1)).IsEqualTo(MOS6502Registers.C);
        await Assert.That(NthDefPhysreg(iAdc3, 0)).IsEqualTo(MOS6502Registers.RC(3));
        await Assert.That(NthDefPhysreg(iAdc3, 1)).IsEqualTo(MOS6502Registers.C);

        // Return copies become identity copies (hint = return physreg).
        await Assert.That(DefPhysreg(iRetA)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysreg(iRetX)).IsEqualTo(MOS6502Registers.X);
        await Assert.That(DefPhysreg(iRetRC2)).IsEqualTo(MOS6502Registers.RC(2));
        await Assert.That(DefPhysreg(iRetRC3)).IsEqualTo(MOS6502Registers.RC(3));

        // Register class table cleared.
        await Assert.That(fn.TryGetVirtualRegisterClass(v0, out _)).IsFalse();
    }

    // Local helpers to keep instruction-build calls terse.
    private static PhysicalRegisterOperand PhysReg(int r, bool def = false, bool isImplicit = false) =>
        new(r, def, isImplicit);

    private static VirtualRegisterOperand VReg(int v, bool def = false) =>
        new(v, def);
}
