using Irie.Dialects.Arith;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// Stage-0 validation of the greedy allocator skeleton (driven via
// RegisterAllocatorPass(useGreedy: true)). The skeleton has the full
// selectOrSplit ladder but minimal rungs (real tryAssign; evict/split stubs;
// spill via the pass's existing machinery), so these exercise tryAssign,
// copy-hint preference, interference correctness, and the spill fall-through —
// the same behaviours the colouring engine's tests pin, proving the greedy path
// produces correct allocations.
public sealed class GreedyRegisterAllocatorTests
{
    private static readonly RegisterAllocatorPass Pass;

    static GreedyRegisterAllocatorTests()
    {
        var target = new MOS6502Target();
        Pass = new RegisterAllocatorPass(target.RegisterInfo, useGreedy: true);
    }

    private static int DefPhysReg(MirInstruction instr) =>
        instr.Operands.OfType<PhysicalReg>().First(p => p.IsDefinition).Id;

    private static MirFunction NewFunction(string name) =>
        new(name, [], IRType.Void);

    // Copy-hint: `%v = pseudo.copy $a` — tryAssign prefers $a (the other end of
    // the copy), so the copy collapses to an identity and no vreg survives.
    [Test]
    public async Task CopyHintFromPhysreg_AssignsHintedRegister()
    {
        var fn = NewFunction("greedy_hint");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(i0.Operands.OfType<VirtualReg>().Any()).IsFalse();
    }

    // No hint — a constant-defined zp value falls back to the first allocatable
    // register in its class (RC0/RC1 reserved, so $zp2 = RC(2)).
    [Test]
    public async Task NoHint_FallsBackToFirstAllocatableInClass()
    {
        var fn = NewFunction("greedy_no_hint");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Imag8, "zp");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true),
            new Immediate(7));

        Pass.Run(fn);

        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.RC(2));
    }

    // Def/use boundary sharing: a value that dies exactly where the next is born
    // does not interfere, so both `ac` values may take the single $a register.
    [Test]
    public async Task ExpiryAtSameSlot_FreesPhysregForNextInterval()
    {
        var fn = NewFunction("greedy_expiry");
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

        await Assert.That(DefPhysReg(i0)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i1)).IsEqualTo(MOS6502Registers.A);
    }

    // Interference correctness: two flexible values both live at the same add
    // must NOT share a register — greedy assigns the first, the second interferes
    // and lands on a different physreg.
    [Test]
    public async Task SimultaneouslyLiveValues_GetDistinctRegisters()
    {
        var fn = NewFunction("greedy_interfere");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true), new Immediate(2));
        // Both v0 and v1 are read here, so both are live across the constant
        // defs → they interfere with each other.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn);

        await Assert.That(DefPhysReg(i0)).IsNotEqualTo(DefPhysReg(i1));
    }

    // Converging eviction (Stage 1): a long-lived flexible value grabs $a via a
    // copy hint, then a short-lived value HARD-pinned to $a (class `ac`) cannot
    // assign. Eviction kicks in — the pinned value is heavier (denser, shorter),
    // so it evicts the flexible value off $a; the evicted value is re-enqueued and
    // relocates to the abundant zero-page file. Both end up placed: this is the
    // converging forced-conflict the Stage-0 stub could not resolve.
    [Test]
    public async Task ConstrainedValueEvictsLighterFlexibleValue_BothPlaced()
    {
        var fn = NewFunction("greedy_evict");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var vx = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        // v0 grabs $a via the copy hint and stays live to the final add.
        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        // Filler that lengthens v0's interval (so it is dequeued first, taking $a).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(vx, IsDefinition: true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(vx, IsDefinition: true),
            new VirtualReg(vx, IsDefinition: false),
            new VirtualReg(vx, IsDefinition: false));
        // v1 is pinned to $a (ac) and short-lived.
        var i3 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true), new Immediate(5));
        // Both v0 and v1 are read here → they interfere, and only $a fits v1.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn);

        // v1 (the pinned, heavier value) wins $a; v0 was evicted to zero page.
        await Assert.That(DefPhysReg(i3)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i0)).IsNotEqualTo(MOS6502Registers.A);
    }

    // Spill rung reachable: two DISTINCT values that must both occupy the single
    // $y register at the SAME instruction is genuinely infeasible. The greedy
    // ladder exhausts assign (no free $y), evict (Stage-0 stub), split (Stage-0
    // stub), reaches the spill rung, and the pass's spill loop detects
    // non-convergence and surfaces a clear error rather than hanging — exercising
    // the greedy → SpilledVregs → SpillVregs path end-to-end.
    //
    // NOTE: a *converging* forced spill (where spilling the right victim makes the
    // function fit) is deferred to Stage 1 — without eviction the Stage-0 skeleton
    // spills the value that failed to assign rather than the cheaper victim the
    // colourer's optimistic-spill cost model would pick, so such cases need the
    // eviction rung. `yc` is used (not a subset of the flexible class) so neither
    // value can be widened off $y.
    [Test]
    public async Task TwoValuesNeedingSameSingleRegSimultaneously_ReachesSpillAndFails()
    {
        var fn = NewFunction("greedy_infeasible");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Yc, "yc");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true), new Immediate(0));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true), new Immediate(1));
        // Both operands of one instruction → both must be live in $y at once.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<InvalidOperationException>();
    }
}
