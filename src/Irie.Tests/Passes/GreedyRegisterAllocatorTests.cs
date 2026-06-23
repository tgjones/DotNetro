using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
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

    // Instruction split (Stage 2): four copy-defined values are each consumed by
    // an absolute store, whose value operand is the narrowing `axy` class
    // ({$a,$x,$y}) — a strict subset of the flexible class. All four are
    // simultaneously live, so they form a 4-clique over a 3-register class:
    // uncolourable by assign+evict alone (every occupant is equal-weight, so none
    // can be evicted). The greedy ladder therefore reaches TrySplit, which
    // instruction-splits a value: its store use is rewritten to a fresh per-use
    // `axy` temp, so the value itself flows only through copies and widens to the
    // flexible class — landing in the abundant zero-page file. Result: three
    // values keep $a/$x/$y, (at least) one relocates to zero page, and allocation
    // converges (the Stage-0 split stub could not resolve this).
    [Test]
    public async Task AxyCliqueExceedingRegisters_InstructionSplitsToZeroPage()
    {
        var fn = NewFunction("greedy_instr_split");
        var bb0 = fn.CreateBlock();
        var defs = new MirInstruction[4];
        var vregs = new int[4];
        for (var i = 0; i < 4; i++)
        {
            vregs[i] = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
            defs[i] = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(vregs[i], IsDefinition: true),
                new Immediate(i));
        }
        // Use all four AFTER all four defs, so all are simultaneously live (a
        // 4-clique), each at an `axy`-narrowing absolute store.
        foreach (var v in vregs)
            bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StAbs),
                new VirtualReg(v, IsDefinition: false),
                new Symbol("g"));

        Pass.Run(fn); // must converge (would throw under the Stage-0 stub).

        var defRegs = defs.Select(DefPhysReg).ToArray();
        var axy = new[] { MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y };
        // Every value is assigned a real register...
        await Assert.That(defRegs.All(r => r >= 0)).IsTrue();
        // ...and at least one value was split off the scarce {$a,$x,$y} class into
        // the zero-page file, which only the splitting rung could achieve.
        await Assert.That(defRegs.Any(r => !axy.Contains(r))).IsTrue();
    }

    // Local split (Stage 2b): a single-block `axy` value fits NO single register
    // over its whole range — $x is busy across its first half, $a across its
    // second half, $y throughout (three precoloured physreg windows) — yet each
    // half CAN be coloured with a different register ($a in the first half, $x in
    // the second). assign fails (no whole-range register), evict fails (the
    // interferences are fixed precoloured windows, not evictable), and instruction
    // split bails (the value's def is an `arith.addi`, not a copy). The greedy
    // ladder therefore reaches local split, which cuts the range at the interior
    // use boundary: the first half stays in the value, the second half moves to a
    // fresh temp via a boundary copy. Reanalysis then colours the halves to $a and
    // $x. Result: allocation converges with NO memory spill, and the two stores
    // land on different registers — only local split achieves this (the spill
    // fallback would emit a `pseudo.spill`).
    [Test]
    public async Task ValueFitsNoSingleRegButHalvesDo_LocalSplitsInsteadOfSpilling()
    {
        var fn = NewFunction("greedy_local_split");
        var bb0 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var sinkX = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var sinkA = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var sinkY = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        void OpenWindow(int physReg) => bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(physReg, IsDefinition: true), new Immediate(9));
        void CloseWindow(int sink, int physReg) => bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(sink, IsDefinition: true), new PhysicalReg(physReg, IsDefinition: false));

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        OpenWindow(MOS6502Registers.Y);              // $y busy across the whole range.
        OpenWindow(MOS6502Registers.X);              // $x busy across the first half.
        // v's def is a non-copy, non-rematerializable op (reads p) so instruction
        // split bails and the spill fallback cannot rematerialize it.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        CloseWindow(sinkX, MOS6502Registers.X);      // $x dies before useA.
        var useA = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StAbs),
            new VirtualReg(v, IsDefinition: false), new Symbol("g"));
        OpenWindow(MOS6502Registers.A);              // $a busy across the second half.
        var useB = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StAbs),
            new VirtualReg(v, IsDefinition: false), new Symbol("g"));
        CloseWindow(sinkA, MOS6502Registers.A);
        CloseWindow(sinkY, MOS6502Registers.Y);

        Pass.Run(fn); // must converge via local split (would otherwise spill).

        // The two stores read DIFFERENT registers — the value was cut, each piece
        // coloured to its own free register.
        var regA = useA.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        var regB = useB.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        await Assert.That(regA).IsNotEqualTo(regB);

        // And no value was spilled to memory — local split kept everything in
        // registers, which the store/reload fallback would not have.
        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);
    }

    // Split-around-call (Stage 3): a value whose class is entirely CALLER-SAVED
    // (`axy` = {$a,$x,$y}) is live ACROSS a call. The call (a `jsr.abs` carrying
    // the caller-saved registers as implicit-DEFs — the clobber barrier) covers
    // the value's every allowed register at the call point, so it cannot stay in
    // any of them across the call. Its def is an `arith.addi` (non-copy →
    // instruction split bails) and it has a single post-call use (one in-block use
    // → local split bails, needing ≥2). assign fails (no whole-range free reg),
    // evict fails (the clobbers are fixed precoloured windows), instruction and
    // local split both bail — so the greedy ladder reaches split-around-call. It
    // relocates the value into a flexible-class temp across the call (copy out
    // before, copy back after — the llvm-mos `sta __rc20` / `lda __rc20` shape);
    // reanalysis homes that temp in a CALLEE-SAVED register (the caller-saved
    // clobber windows force it there) rather than spilling to memory.
    //
    // Discriminator: SOME instruction defines a callee-saved register (RC20..31) —
    // the across-call relocation home — AND no value was spilled to memory. Only
    // split-around-call parks a value in a preserved register; a plain spill would
    // emit a `pseudo.spill` and touch no callee-saved register at all.
    [Test]
    public async Task ValueLiveAcrossCall_SplitsIntoCalleeSavedRegister()
    {
        var fn = NewFunction("greedy_across_call");
        var bb0 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        // v is hard-pinned to axy (all caller-saved) by its store use; its def is a
        // non-copy arith.addi so instruction split cannot widen it.
        var v = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Axy, "axy");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));

        // The call: clobbers every caller-saved register as implicit-defs (the
        // barrier CallLowering attaches to a jsr.abs) — $a/$x/$y plus the
        // caller-saved zero-page scratch RC2..RC19. Only the callee-saved RC20..31
        // survive, so the across-call relocation temp can ONLY land there. v is
        // live across it.
        var clobbers = new List<MirOperand> { new Symbol("g") };
        foreach (var caller in new[] { MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y }
                     .Concat(Enumerable.Range(2, 18).Select(MOS6502Registers.RC)))
            clobbers.Add(new PhysicalReg(caller, IsDefinition: true, IsImplicit: true));
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.JsrAbs), clobbers.ToArray());

        // The single post-call use — one in-block use, so local split bails.
        var use = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StAbs),
            new VirtualReg(v, IsDefinition: false), new Symbol("g"));

        Pass.Run(fn); // must converge via split-around-call (else it would spill).

        // The post-call store reads a real GPR (the value's class is axy, satisfied
        // by the post-call copy-back) — the relocation copied it back out of the
        // callee-saved home.
        var useReg = use.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        var axy = new[] { MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y };
        await Assert.That(axy.Contains(useReg)).IsTrue();

        // The discriminator: some instruction DEFINES a callee-saved register — the
        // across-call relocation home the temp landed in. A plain spill touches no
        // callee-saved register; only split-around-call parks the value there.
        var calleeSaved = Enumerable.Range(20, 12).Select(MOS6502Registers.RC).ToArray();
        var defsCalleeSaved = fn.Blocks.SelectMany(b => b.Instructions)
            .SelectMany(i => i.Operands)
            .OfType<PhysicalReg>()
            .Any(p => p.IsDefinition && calleeSaved.Contains(p.Id));
        await Assert.That(defsCalleeSaved).IsTrue();

        // And nothing was spilled to memory — only the split-around-call relocation
        // achieves this; the store/reload fallback would emit a `pseudo.spill`.
        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);
    }

    // Split-around-clobber (Stage 3b): two values are each produced by a non-copy
    // def hard-pinned to the single-physreg class `ac` ({$a}) — the shape of an
    // adc/sbc/load result in an i16/i32 chain. The FIRST value (`v0`) is live
    // ACROSS the second's def (it is read AFTER `v1` is defined), so two `ac`
    // values are simultaneously live → they cannot share the one $a register.
    // assign fails ($a busy across `v1`'s re-def), evict fails (`v1`'s def is a
    // constrained def, not an evictable committed vreg), instruction-split bails
    // (`v0`'s def is a non-copy `arith.addi`, not a copy), local-split bails (the
    // conflict is a constrained re-def, not a free-register gap), and split-around-
    // call bails (no implicit-def clobber barrier). So the greedy ladder reaches
    // split-around-clobber, which relocates `v0` off $a: it mints a flexible temp
    // right after `v0`'s def, copies `v0` into it, and redirects the later use to
    // the temp. Reanalysis homes the temp in the zero-page file; `v0` occupies $a
    // only for the brief def→copy window. This is exactly what the deleted isel
    // out-funnel / InsertRelocationCopiesForConstrainedDefs did — the kind that
    // unblocks Stage 4.
    //
    // Discriminators vs. a plain spill: (1) allocation converges (the Stage-3
    // ladder without this kind would spill and re-spill, tripping the round cap);
    // (2) NO `pseudo.spill` is emitted — the relocation kept everything in
    // registers; (3) some value is homed in the flexible zero-page file (the
    // relocation target), not on the scarce $a.
    [Test]
    public async Task ConstrainedDefLiveAcrossReClobber_SplitsToFlexibleClass()
    {
        var fn = NewFunction("greedy_split_clobber");
        var bb0 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        // Two values each pinned to the single-physreg class `ac` by a non-copy
        // def (the adc-result shape). Both want $a.
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var sink = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        // v0 = non-copy def, pinned to $a.
        var d0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // v1 = non-copy def, ALSO pinned to $a — re-clobbers $a while v0 is live.
        var d1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // Both v0 (live across v1's def) and v1 are read here → they interfere and
        // both want $a. v0 must vacate $a before v1's def.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(sink, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        Pass.Run(fn); // must converge via split-around-clobber (else it would spill).

        // v1 keeps $a (the constrained re-def winner); v0 was relocated off $a.
        await Assert.That(DefPhysReg(d1)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(d0)).IsEqualTo(MOS6502Registers.A);

        // The discriminator: NO value was spilled to memory — the relocation kept
        // everything in registers. A plain spill would emit a `pseudo.spill`.
        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);

        // And some value landed in the flexible zero-page file (the relocation
        // target) — the relocated copy of v0 lives there, off the scarce $a.
        var zpRegs = Enumerable.Range(2, 30).Select(MOS6502Registers.RC).ToArray();
        var anyInZp = fn.Blocks.SelectMany(b => b.Instructions)
            .SelectMany(i => i.Operands)
            .OfType<PhysicalReg>()
            .Any(r => zpRegs.Contains(r.Id));
        await Assert.That(anyInZp).IsTrue();
    }

    // Spill rung reachable: two DISTINCT values that must both occupy the single
    // $c (carry-flag) register at the SAME instruction is genuinely infeasible. The
    // greedy ladder exhausts assign (no free $c), evict (no lighter victim), every
    // split kind (instruction split needs a copy def; split-around-clobber needs
    // the pinned reg to be a FLEXIBLE-class member, and $c is NOT — a flag cannot
    // live in a general register / zero page; local split needs a free-register
    // gap; split-around-call needs a clobber barrier), reaches the spill rung, and
    // the pass's spill loop detects non-convergence and surfaces a clear error
    // rather than hanging — exercising the greedy → SpilledVregs → SpillVregs path
    // end-to-end.
    //
    // The carry-flag class `cc` ({$c}) is the vehicle precisely because $c is the
    // one single-physreg class that is NOT a subset of the flexible 8-bit class, so
    // NO split kind can relocate either value off $c. (A `yc`/`ac` value, by
    // contrast, now relocates via split-around-clobber — $y/$a ARE flexible-class
    // members — so those classes no longer produce an infeasible conflict.)
    [Test]
    public async Task TwoValuesNeedingSameSingleRegSimultaneously_ReachesSpillAndFails()
    {
        var fn = NewFunction("greedy_infeasible");
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true), new Immediate(0));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, IsDefinition: true), new Immediate(1));
        // Both operands of one instruction → both must be live in $c at once, and
        // neither can relocate off $c (it is not a flexible-class member).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));

        await Assert.That(() => Pass.Run(fn)).Throws<InvalidOperationException>();
    }

    // Split-around-clobber, CROSS-BLOCK donor (Stage R1 — the slot-precise edit).
    //
    // This is the synthetic two-address adc-chain shape from the Stage-R0 Mode-B
    // non-termination, reduced to its essence: a value `v0` pinned to the single-
    // physreg class `ac` ({$a}) is live ACROSS a later re-definition of $a (`v1`,
    // also `ac`) AND is used in a SUCCESSOR block. The donor's across-clobber use
    // lives in bb1, not bb0.
    //
    // Before Stage R1, RelocateAcrossClobber's redirect loop was block-local: it
    // rewrote only `v0`'s uses inside bb0, so the bb1 use kept `v0` live across the
    // re-clobber and the donor range NEVER shrank — the constrained-def-result
    // split fired every round until the round cap (the pass throws "did not
    // converge"). The slot-precise edit redirects EVERY use of the donor value in
    // the across-clobber slot range across ALL blocks, including bb1, so the donor
    // range strictly shrinks and allocation converges.
    //
    // Discriminators: (1) Pass.Run does NOT throw (the old block-local edit would
    // trip the round cap); (2) NO `pseudo.spill` is emitted (the relocation kept
    // everything in registers); (3) the bb1 use reads a flexible zero-page register
    // (the relocated temp), NOT $a — proving the cross-block use was redirected off
    // the constrained register.
    [Test]
    public async Task ConstrainedDefLiveAcrossReClobber_CrossBlockUse_RedirectsAndConverges()
    {
        var fn = NewFunction("greedy_split_clobber_xblock");
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        // Two values each pinned to `ac` by a non-copy def (the adc-result shape).
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var sink = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var xsink = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        // v0 = non-copy def, pinned to $a (the donor — live across v1's def AND
        // into bb1).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // v1 = non-copy def, ALSO pinned to $a — re-clobbers $a while v0 is live.
        var d1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // v1 is consumed in bb0 (it really needs $a here).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(sink, IsDefinition: true),
            new VirtualReg(v1, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb1, []));

        // The CROSS-BLOCK use of v0 — the use the old block-local redirect missed,
        // which kept v0 live across v1's re-clobber forever.
        var xuse = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(xsink, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));

        Pass.Run(fn); // must converge via the slot-precise cross-block relocation.

        // No value was spilled to memory — the relocation kept everything in
        // registers. (The old block-local edit looped to the round cap and threw.)
        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);

        // v1 keeps $a; the bb1 use of v0's value reads a flexible zero-page register
        // (the relocated temp), NOT $a — the cross-block use was redirected off the
        // constrained register.
        await Assert.That(DefPhysReg(d1)).IsEqualTo(MOS6502Registers.A);
        var xuseReg = xuse.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        await Assert.That(xuseReg).IsNotEqualTo(MOS6502Registers.A);
    }

    // Split-not-spill across a PRECOLOURED physreg clobber (Stage R2 — the chain
    // blocker, distinct from the vreg-re-clobber kinds). A value `v0:ac` (allowed
    // {$a}) is live ACROSS a later DIRECT physreg def of $a — a precoloured
    // `$a = pseudo.copy …` — and is then read at its own use. This is the i16
    // high-adc-result shape: the result is pinned to $a, but the ABI return move
    // for the LOW byte (`$a = copy …`, emitted LSB-first) clobbers $a while the
    // high byte is still needed for its own return move.
    //
    // FindLaterReClobber does NOT catch this (the re-clobber is a physreg def, not
    // another {$a}-pinned vreg), so without the relocatable-across-phys-clobber
    // rung `v0` exhausts assign (its only register $a is precoloured-busy across
    // its range), evict (the clobber is a fixed precoloured window), and every
    // other split kind — and would SPILL to memory, then re-spill to the round cap
    // (a {$a}-only reload temp does not relieve the contention). The new rung
    // relocates `v0`'s across-clobber use into a flexible temp, so `v0` occupies $a
    // only for the brief def→copy window.
    //
    // Discriminators: (1) Pass.Run does not throw (no round-cap spill loop); (2) no
    // `pseudo.spill` is emitted; (3) the across-clobber use reads a register that
    // is NOT $a — the value was relocated off the clobbered register.
    [Test]
    public async Task ConstrainedValueLiveAcrossPrecolouredPhysClobber_SplitsNotSpills()
    {
        var fn = NewFunction("greedy_phys_clobber_split");
        var bb0 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        // v0 = non-copy def, pinned to $a.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // A DIRECT precoloured def of $a while v0 is still live (the LSB-first ABI
        // return move that clobbers $a). v0 is read AFTER this, so it lives across
        // the clobber.
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false));
        // v0's across-clobber use — needs v0's value AFTER $a was clobbered.
        var use = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.StAbs),
            new VirtualReg(v0, IsDefinition: false), new Symbol("g"));

        Pass.Run(fn); // must converge via split-not-spill (else it spills to memory).

        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);

        // The across-clobber use reads a register that is NOT $a — v0's value was
        // relocated off the clobbered register into a flexible temp.
        var useReg = use.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        await Assert.That(useReg).IsNotEqualTo(MOS6502Registers.A);
    }

    // Split-product GPR preference (Stage R2 — the tay/tya shape). When a value is
    // relocated off a constrained register, the relocation temp must prefer a real
    // GPR ($a/$x/$y) ahead of the abundant zero-page pool — the copy-cost
    // preference that makes the relocation a `tay`/`tya` register move rather than
    // a `sta`/`lda` memory round-trip (llvm-mos getRegAllocationHints).
    //
    // The across-clobber use here is an `arith.addi` OPERAND (a zp-capable use with
    // NO physreg copy-hint), so the relocation temp carrying v0 is full `any8` (zp
    // AND GPRs both legal) AND has no GPR hint to steer it. Irie's `any8` order
    // lists zp first (it is abundant), so without the split-product GPR preference
    // the temp would land in zero page; the preference steers it to a real GPR.
    [Test]
    public async Task RelocationSplitProduct_PrefersRealGprOverZeroPage()
    {
        var fn = NewFunction("greedy_reloc_gpr_pref");
        var bb0 = fn.CreateBlock();

        var p = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var sink = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(p, IsDefinition: true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false),
            new VirtualReg(p, IsDefinition: false));
        // Direct precoloured $a clobber (the LSB-first ABI return move).
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
            new VirtualReg(p, IsDefinition: false));
        // v0's across-clobber use is an `arith.addi` operand — zp-capable, no GPR
        // hint, so the relocation temp is full `any8` and the preference decides.
        var use = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(sink, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));

        Pass.Run(fn);

        // The value v0 reads at the across-clobber addi is the relocated temp; its
        // home must NOT be the zero-page pool (a zp landing would mean the
        // split-product preference did not steer it — the chains would then emit
        // sta/lda not tay/tya).
        var useReg = use.Operands.OfType<PhysicalReg>().First(o => !o.IsDefinition).Id;
        var zpRegs = Enumerable.Range(2, 30).Select(MOS6502Registers.RC).ToArray();
        await Assert.That(zpRegs.Contains(useReg)).IsFalse();
    }

    // Eviction-by-cost: preemptive eviction of a reassignable HEAVIER incumbent
    // (Stage R2). A value HARD-pinned to a single register ($a, class `ac`)
    // interferes with a longer-lived FLEXIBLE incumbent that grabbed $a first via a
    // copy hint. The incumbent is made HEAVIER (more uses → higher spill weight)
    // AND it is not live across the pinned value's def (so the R1 hard-claim
    // exclusion does not pre-empt the conflict). By the default policy the pinned
    // value could only evict a strictly-LIGHTER incumbent, so the weight gate alone
    // blocks the eviction; and the pinned value cannot SPLIT (its conflict is pure
    // simultaneous interference, not an across-clobber); and it is not
    // rematerializable (its def reads a register). So WITHOUT eviction-by-cost it
    // would spill to memory. The rule (llvm-mos canEvictInterferenceBasedOnCost: a
    // value with ONE option may evict a REASSIGNABLE incumbent regardless of
    // weight) evicts the flexible incumbent — which reassigns to the abundant zero
    // page — so both place. Cascade loop-prevention keeps it from cycling.
    //
    // Discriminator: the pinned value wins $a, the incumbent is relocated off it,
    // and NO value is spilled to memory.
    [Test]
    public async Task PinnedValueEvictsReassignableHeavierIncumbent_ByCost()
    {
        var fn = NewFunction("greedy_evict_by_cost");
        var bb0 = fn.CreateBlock();
        var q = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var s1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var s2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var s3 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        // A source so v1's def reads a register — v1 is NOT rematerializable (the
        // spill rung cannot dodge the conflict by recomputing it).
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(q, IsDefinition: true), new Immediate(3));
        // v1 (pinned $a) is defined FIRST and short-lived — so v0 (defined later) is
        // not live across v1's def and the R1 hard-claim exclusion does not fire.
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(q, IsDefinition: false),
            new VirtualReg(q, IsDefinition: false));
        // v0 grabs $a via the copy hint and stays live PAST v1 (longer interval →
        // dequeued first → grabs $a; v1 must then evict it).
        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        // v0 and v1 both read here → they interfere; only $a fits v1.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(s1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v1, IsDefinition: false));
        // Many MORE uses of v0 → v0 is HEAVIER than v1 (higher use density), so the
        // default strictly-lighter eviction gate REFUSES to evict it — only the
        // by-cost rule (v1 one-option, v0 reassignable) can.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(s2, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(s3, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));

        Pass.Run(fn); // converges ONLY via eviction-by-cost.

        var spills = fn.Blocks.SelectMany(b => b.Instructions).Count(i =>
            i.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)i.Opcode.Code == PseudoOp.Spill);
        await Assert.That(spills).IsEqualTo(0);

        // v1 (pinned, single-option) wins $a; v0 was evicted to a reassignable reg.
        await Assert.That(DefPhysReg(i1)).IsEqualTo(MOS6502Registers.A);
        await Assert.That(DefPhysReg(i0)).IsNotEqualTo(MOS6502Registers.A);
    }
}
