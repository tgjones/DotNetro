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

    // Two DISTINCT values that must both occupy the single $y register at the
    // SAME instruction is genuinely infeasible — no allocation exists (you
    // cannot hold two live values in one register simultaneously), and not even
    // spilling can fix it (rematerializing one still leaves both live at the
    // shared use). Phase 4's spilling loop detects non-convergence and surfaces
    // a clear InvalidOperationException rather than the old NotImplementedException
    // or an infinite loop.
    //
    // `yc` is used rather than `ac` because `yc` is not a subset of the flexible
    // class, so the colourer cannot widen it: both vregs are hard-pinned to $y.
    [Test]
    public async Task TwoValuesNeedingSameSingleRegSimultaneously_FailsToConverge()
    {
        var fn = NewFunction("infeasible");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true),
            new Immediate(0));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true),
            new Immediate(1));
        // Both operands of one instruction → both must be live in $y at once.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<InvalidOperationException>();
    }

    // -------------------------------------------------------------------------
    // Spilling (Phase 4)
    // -------------------------------------------------------------------------

    // Forced store/reload spill: two NON-rematerializable values pinned to the
    // single-$y class `yc`, live across each other at two SEPARATE use points,
    // so the colourer cannot place both. The cheaper one is spilled. Because the
    // values are produced by register-reading ops (arith.addi, not a constant)
    // they are NOT rematerializable, so the spill takes the store/reload path:
    // a `pseudo.spill` after the def and a `pseudo.reload` before the use, and a
    // fresh abstract FrameSlot is minted on the function.
    [Test]
    public async Task ForcedSpill_NonRematValue_EmitsStoreReloadAndFrameSlot()
    {
        var fn = NewFunction("forced_spill");
        var bb0 = fn.CreateBlock();

        // Two source values in flexible class so they can live anywhere.
        var s0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var s1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(s0, IsDefinition: true), new Immediate(3));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(s1, IsDefinition: true), new Immediate(4));

        // Two yc values, each defined by a register-reading op (so non-remat),
        // and kept live across one another.
        var a = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        var b = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(a, IsDefinition: true),
            new VirtualReg(s0, IsDefinition: false),
            new VirtualReg(s1, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(b, IsDefinition: true),
            new VirtualReg(s0, IsDefinition: false),
            new VirtualReg(s1, IsDefinition: false));
        // Use a, then b — both live across the gap (a defined first, used last).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc"), IsDefinition: true),
            new VirtualReg(b, IsDefinition: false),
            new VirtualReg(b, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc"), IsDefinition: true),
            new VirtualReg(a, IsDefinition: false),
            new VirtualReg(a, IsDefinition: false));

        Pass.Run(fn);

        // A spill slot was minted, and store/reload ops were emitted.
        await Assert.That(fn.FrameSlots.Count).IsGreaterThanOrEqualTo(1);

        var ops = bb0.Instructions
            .Where(i => i.Opcode.Dialect == PseudoDialect.Id)
            .Select(i => (PseudoOp)i.Opcode.Code)
            .ToList();
        await Assert.That(ops).Contains(PseudoOp.Spill);
        await Assert.That(ops).Contains(PseudoOp.Reload);
    }

    // Rematerialization beats store/reload: a spilled value whose def reads no
    // registers (a constant) is recomputed at its use rather than stored and
    // reloaded. No pseudo.reload / pseudo.spill and no FrameSlot is emitted; the
    // constant's defining op is cloned before each use instead.
    [Test]
    public async Task Spill_RematerializableConstant_RecomputesWithoutReload()
    {
        var fn = NewFunction("remat");
        var bb0 = fn.CreateBlock();

        // One $a value (so $a is occupied across the region) plus several yc
        // constants kept simultaneously live at distinct points to force a spill
        // of a constant. We arrange two yc constants live across one another.
        var c0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        var c1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(c0, IsDefinition: true), new Immediate(5));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(c1, IsDefinition: true), new Immediate(6));
        // Use c0 (keeps it live past c1's def), then use c1 — they interfere but
        // at separate use points, so spilling ONE of them (remat) makes it fit.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc"), IsDefinition: true),
            new VirtualReg(c1, IsDefinition: false),
            new VirtualReg(c1, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc"), IsDefinition: true),
            new VirtualReg(c0, IsDefinition: false),
            new VirtualReg(c0, IsDefinition: false));

        Pass.Run(fn);

        // Rematerialized: no store/reload, no frame slot.
        await Assert.That(fn.FrameSlots.Count).IsEqualTo(0);
        var pseudoOps = bb0.Instructions
            .Where(i => i.Opcode.Dialect == PseudoDialect.Id)
            .Select(i => (PseudoOp)i.Opcode.Code)
            .ToList();
        await Assert.That(pseudoOps).DoesNotContain(PseudoOp.Reload);
        await Assert.That(pseudoOps).DoesNotContain(PseudoOp.Spill);
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
    // Both %0 and %1 are SHORT-lived (they span no arithmetic chain op — the
    // addi here is the pre-isel generic op, not a tied two-address op), so the
    // Phase-5 cost-driven preference promotes the scarce GPRs ($x/$y/$a) ahead of
    // the zero-page pool: with $a taken by %0, %1 lands in $x. (Before Phase 5 the
    // class's fixed zp-first order sent %1 to $zp2; the GPR-first preference for
    // short values is exactly the "$y gap" closure this phase targets.)
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

        // First livein keeps its $a hint; the second is a short-lived flexible
        // value, so the Phase-5 GPR-first preference picks the next free GPR after
        // $a — that is $x (the short-range GPR order is $x, $y, $a).
        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i1)).IsEqualTo(MOS6502Registers.X);
    }

    // Spill-cost chooses the CHEAPEST node. Two non-rematerializable values
    // compete for the single $y register across a region; one is used ONCE, the
    // other is used MANY times. The spill-cost heuristic (def+use count weighted
    // by loop depth, here flat) must spill the cheap (used-once) value, leaving
    // the heavily-used value in the register. The minted frame slot's name
    // encodes the spilled vreg id, so we assert it is the cheap one.
    [Test]
    public async Task SpillCost_SpillsTheCheapestValue()
    {
        var fn = NewFunction("cheapest");
        var bb0 = fn.CreateBlock();

        var s0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var s1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(s0, IsDefinition: true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(s1, IsDefinition: true), new Immediate(2));

        // cheap: defined by a register-reading op (non-remat), used exactly once.
        var cheap = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        // heavy: same, but used several times.
        var heavy = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(cheap, IsDefinition: true),
            new VirtualReg(s0, IsDefinition: false),
            new VirtualReg(s1, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(heavy, IsDefinition: true),
            new VirtualReg(s0, IsDefinition: false),
            new VirtualReg(s1, IsDefinition: false));

        // heavy used many times (keeps it live and expensive to spill). Its
        // result vregs are in the flexible class so they add no $y pressure —
        // only `cheap` and `heavy` contend for the single $y register.
        for (var k = 0; k < 4; k++)
            bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8"), IsDefinition: true),
                new VirtualReg(heavy, IsDefinition: false),
                new VirtualReg(s0, IsDefinition: false));
        // …then cheap used once, at the very end (so cheap and heavy interfere).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8"), IsDefinition: true),
            new VirtualReg(cheap, IsDefinition: false),
            new VirtualReg(s0, IsDefinition: false));

        Pass.Run(fn);

        // Exactly the cheap value was spilled to a frame slot.
        await Assert.That(fn.FrameSlots.Any(s => s.SymbolName.EndsWith($"_spill{cheap}"))).IsTrue();
        await Assert.That(fn.FrameSlots.Any(s => s.SymbolName.EndsWith($"_spill{heavy}"))).IsFalse();
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
