using Irie.Dialects.Arith;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

public sealed class RegisterAllocatorTests
{
    private static readonly RegisterAllocatorPass Pass;

    static RegisterAllocatorTests()
    {
        // The MOS6502 target's constructor registers all four core/arith/cf/
        // pseudo dialects (via MirBootstrap) and the mos6502 dialect itself,
        // which the RA needs to introspect pseudo.copy operands.
        var target = new MOS6502Target();
        Pass = new RegisterAllocatorPass(target.RegisterInfo);
    }

    // Helper: first physreg def in instruction.
    private static int DefPhysReg(MirInstruction instr) =>
        instr.Operands.OfType<PhysicalReg>().First(p => p.IsDefinition).Id;

    // Helper: physreg use (non-def) at the given operand index.
    private static int UsePhysRegAt(MirInstruction instr, int idx) =>
        ((PhysicalReg)instr.Operands[idx]).Id;

    // Helper: build an instruction-list-only function (no parameters, no
    // signature concerns) so RA tests can focus on the allocation behaviour.
    private static MirFunction NewFunction(string name) =>
        new(name, [], IRType.Void);

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    // Coalescing a vreg with its source physreg — `%v = pseudo.copy $a`
    // coalesces %v onto $a (George's test trivially passes: %v has no other
    // interference), turning the copy into an identity `$a = pseudo.copy $a`.
    // This is the graph-colouring coalescer subsuming the old "copy hint".
    //
    //   %0:ac = pseudo.copy $a    →  $a = pseudo.copy $a
    [Test]
    public async Task HintFromPhysregCopy_AssignsHintedRegister()
    {
        var fn = NewFunction("hint_livein");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(i0.Operands.OfType<VirtualReg>().Any()).IsFalse();
    }

    // No copy hint — a vreg with no livein-form pseudo.copy at its def site
    // falls back to the first allocatable register in its class. For the
    // `zp` (Imag8) class that is `zp2` (RC0/RC1 reserved as soft stack
    // pointer per CC_MOS).
    [Test]
    public async Task NoHint_FallsBackToFirstAllocatableInClass()
    {
        var fn = NewFunction("no_hint");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Imag8, "zp");

        // arith.constant materializes a vreg without a copy-from-physreg
        // hint, exercising the "fall back to first allocatable" path.
        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true),
            new Immediate(7));

        Pass.Run(fn);

        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.RC(2));
    }

    // Def/use boundary sharing: a value that DIES exactly where the next is
    // BORN does not interfere with it (the half-open sub-slot numbering makes
    // the producer's segment end at the def point where the consumer's begins),
    // so both can take the same single physreg.
    //
    //   %0:ac = pseudo.copy $a    ; %0 range [.., defslot0]
    //   %1:ac = arith.addi %0, %0 ; %0 last use, %1 defined here
    //
    // %0 coalesces onto $a (copy from $a). %1 is `ac` (touched by the non-copy
    // arith.addi, so NOT widened — it keeps its single-physreg class) and must
    // also take $a, the only allocatable in `ac`. Because %0 and %1 do not
    // interfere at the boundary, the colourer can give both $a; if they DID
    // interfere there would be no second `ac` register and the pass would throw.
    [Test]
    public async Task ExpiryAtSameSlot_FreesPhysregForNextInterval()
    {
        var fn = NewFunction("expiry");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));

        Pass.Run(fn);

        // %0 → $a via hint; %1 → $a after %0 expires at slot 1.
        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i1)).IsEqualTo(MOS6502Registers.A);
    }

    // Two interfering vregs in a single-register class (`yc`, which contains
    // only $y) → optimistic select finds no free colour for the second →
    // NotImplementedException (Phase 4 turns this into a real spill).
    //
    // `yc` is used rather than `ac` because `yc` is not a subset of the flexible
    // class, so the colourer cannot widen it: both vregs are hard-pinned to $y,
    // and since they interfere one is uncolourable — exercising the genuine
    // out-of-registers select failure.
    [Test]
    public async Task TwoOverlappingIntervalsInSingleRegClass_ThrowsNotImplemented()
    {
        var fn = NewFunction("spill");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true),
            new Immediate(0));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true),
            new Immediate(1));
        // Use both so their ranges overlap.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<NotImplementedException>();
    }

    // Two values that both copy from $a but interfere cannot both take $a; the
    // second falls back to the next allocatable in its class.
    //
    //   %0:any8 = pseudo.copy $a    ; both copied from $a, but…
    //   %1:any8 = pseudo.copy $a    ; …%0 and %1 are both live at the addi,
    //   %2:ac   = arith.addi %0, %1 ; …so they interfere.
    //
    // %0 coalesces onto $a. %1 also prefers $a (copy bias) but interferes with
    // %0 (now $a), so the colourer skips $a and picks the next allowed colour.
    // `any8`'s allocatable order puts zp2..zp31 ahead of $a/$x, so %1 → $zp2.
    [Test]
    public async Task HintSuppressedWhenPhysregBusy_FallsBackToNextAllocatable()
    {
        var fn = NewFunction("busy_hint");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn);

        // First livein keeps its $a hint; the second fills the next free slot.
        // `any8`'s allocatable order puts zp2..zp31 ahead of $a and $x, so the
        // first free physreg after $a is busy is $zp2.
        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i1)).IsEqualTo(MOS6502Registers.RC(2));
    }

    // After RA: vreg-annotation table is cleared and every operand is a
    // PhysicalReg. The constraint-fixup / result-preservation steps may
    // insert extra pseudo.copy instructions, but the post-RA shape must be
    // physreg-only.
    [Test]
    public async Task PostRA_VRegTableClearedAndOperandsArePhysregs()
    {
        var fn = NewFunction("post_ra");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        // No VRegAnnotation remains for the rewritten vreg.
        await Assert.That(fn.TryGetVRegAnnotation(v0, out _)).IsFalse();

        foreach (var instr in bb0.Instructions)
            await Assert.That(instr.Operands.OfType<VirtualReg>().Any()).IsFalse();
    }

    // Regression: a DEAD livein copy (`%v = pseudo.copy $a`, with %v never
    // used — the shape AbiLowering emits for an ignored calling-convention
    // argument byte) must be assigned its OWN source register so it becomes an
    // identity copy CopyElimination deletes. It must NOT be given a fresh
    // register, because a copy still *writes* its destination: parking the dead
    // copy on $zp2 would clobber whatever live value occupies $zp2 (the bug seen
    // in the runtime WriteLineInt32, whose i32 parameter is unused — the dead
    // param-byte copies landed on $zp4 and overwrote the real working bytes).
    //
    //   %dead = pseudo.copy $a       ; %dead never used
    //   %home = arith.constant 7     ; a real value RA parks in $zp2
    //   ... use %home ...
    // The dead copy must resolve to `$a = pseudo.copy $a` (identity), leaving
    // $zp2 untouched for %home.
    [Test]
    public async Task DeadLiveinCopy_AssignedSourceReg_DoesNotClobber()
    {
        var fn = NewFunction("dead_livein");
        var bb0 = fn.CreateBlock();

        var dead = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var home = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        // Dead livein copy: defines `dead` from $a; `dead` is never used.
        var deadCopy = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(dead, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        // A real value that RA must keep in a distinct register, used after.
        var homeDef = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(home, IsDefinition: true),
            new Immediate(7));
        var homeUse = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8"), IsDefinition: true),
            new VirtualReg(home, IsDefinition: false),
            new VirtualReg(home, IsDefinition: false));

        Pass.Run(fn);

        // The dead copy resolved to an identity ($a = pseudo.copy $a): its def
        // physreg equals its source physreg ($a).
        await Assert.That(DefPhysReg(deadCopy)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(UsePhysRegAt(deadCopy, 1)).IsEqualTo(MOS6502Registers.A);

        // The real value got its own (non-$a) register, NOT clobbered by the
        // dead copy.
        await Assert.That(DefPhysReg(homeDef)).IsNotEqualTo(MOS6502Registers.A);
        await Assert.That(UsePhysRegAt(homeUse, 1)).IsEqualTo(DefPhysReg(homeDef));
    }

    // Move-self-interference suppression (Phase 3): a dead copy `%dead = copy $a`
    // whose SOURCE $a is STILL LIVE afterwards (read by a later instruction) was
    // the exact WriteLineInt32 miscompile. The raw LiveIntervals graph reports
    // %dead and $a as interfering (%dead's one-slot range sits inside $a's live
    // range), which would stop the coalescer from merging them and force %dead
    // onto a FRESH register — and `$fresh = copy $a` would then clobber whatever
    // live value sat in $fresh. The coalescer must recognise that this overlap is
    // attributable solely to the copy itself and merge %dead onto $a, making the
    // copy an identity. Here $a is read again by the trailing arith.addi, so $a
    // is genuinely live across the dead copy.
    //
    //   %dead = pseudo.copy $a       ; %dead never used; $a still live below
    //   %r    = arith.addi $a, $a    ; reads $a (so $a is live across the copy)
    // The dead copy must resolve to `$a = pseudo.copy $a`.
    [Test]
    public async Task DeadCopy_SourcePhysregStillLive_CoalescesToIdentity()
    {
        var fn = NewFunction("dead_live_src");
        var bb0 = fn.CreateBlock();

        var dead = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var result = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var deadCopy = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(dead, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        // Reads $a directly AFTER the dead copy, keeping $a live across it.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(result, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        // The dead copy is an identity ($a = pseudo.copy $a), NOT a fresh-reg
        // write that would clobber a live value.
        await Assert.That(DefPhysReg(deadCopy)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(UsePhysRegAt(deadCopy, 1)).IsEqualTo(MOS6502Registers.A);
    }
}
