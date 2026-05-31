using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes.Analyses;

public sealed class LivenessAnalysisTests
{
    static LivenessAnalysisTests()
    {
        // Ensure the four core/arith/cf/pseudo dialects are registered before
        // any test constructs an OpcodeRef referring to them. The MOS6502
        // target's constructor doubles as a guard so the target dialect is
        // also available if a test needs it.
        _ = new MOS6502Target();
    }

    private static readonly LivenessAnalysis Analysis = new();

    // Single block:
    //   %0 = pseudo.copy $a    ; slot 0 — def %0
    //   %1 = pseudo.copy %0    ; slot 1 — use %0, def %1
    //
    // No successors so LiveIn/LiveOut are both empty.
    // %0 is live [0, 1]; %1 is defined at slot 1 with no uses, range [1, 1].
    [Test]
    public async Task SingleBlock_RangesReflectDefAndLastUse()
    {
        var fn = new MirFunction("single", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false));

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEmpty();

        await Assert.That(liveness.SlotOf[i0]).IsEqualTo(0);
        await Assert.That(liveness.SlotOf[i1]).IsEqualTo(1);

        // %0: defined at slot 0, last used at slot 1.
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 1));
        // %1: defined at slot 1, no uses — range covers the def only.
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(1, 1));
    }

    // Two sequential blocks — %0 defined in bb0, used in bb1:
    //
    //   bb0:
    //     %0 = pseudo.copy $a    ; slot 0 — def %0
    //     cf.br bb1              ; slot 1
    //   bb1:
    //     %1 = pseudo.copy %0    ; slot 2 — use %0, def %1
    //
    // Dataflow: LiveIn[bb1] = {%0}, LiveOut[bb0] = {%0}.
    // %0 range widened by LiveOut[bb0] to slot 1 and by LiveIn[bb1] to slot 2:
    //   range [0, 2] (def at 0, end of bb0 at 1, use in bb1 at 2).
    [Test]
    public async Task TwoBlocks_CrossBlockRangeWidened()
    {
        var fn = new MirFunction("two", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br),
            new BlockTarget(bb1, []));

        bb1.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false));

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEquivalentTo(new[] { v0 });

        await Assert.That(liveness.LiveIn[bb1]).IsEquivalentTo(new[] { v0 });
        await Assert.That(liveness.LiveOut[bb1]).IsEmpty();

        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 2));
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(2, 2));
    }

    // Self-loop — tests fixed-point convergence:
    //
    //   bb0:
    //     %0 = pseudo.copy $a    ; slot 0 — def %0
    //     cf.br bb1              ; slot 1
    //   bb1:
    //     %1 = pseudo.copy %0    ; slot 2 — use %0, def %1
    //     cf.br bb1              ; slot 3 (self-loop)
    //
    // LiveIn[bb1] = {%0} (upward-exposed), LiveOut[bb1] = {%0} (from self-loop).
    // %0 range covers bb0 + entire bb1 body: [0, 3].
    [Test]
    public async Task SelfLoop_LivenessConvergesAndRangeCoversFull()
    {
        var fn = new MirFunction("loop", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br),
            new BlockTarget(bb1, []));

        bb1.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false));
        bb1.AddInstruction(CfDialect.OpRef(CfOp.Br),
            new BlockTarget(bb1, []));

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEquivalentTo(new[] { v0 });

        await Assert.That(liveness.LiveIn[bb1]).IsEquivalentTo(new[] { v0 });
        await Assert.That(liveness.LiveOut[bb1]).IsEquivalentTo(new[] { v0 });

        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 3));
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(2, 2));
    }

    // Empty function (no blocks) — should not throw, should return empty maps.
    [Test]
    public async Task EmptyFunction_NoRanges()
    {
        var fn = new MirFunction("empty", [], IRType.Void);
        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.RangeOf.Count).IsEqualTo(0);
        await Assert.That(liveness.SlotOf.Count).IsEqualTo(0);
    }

    // Block parameters count as defs at block entry — the parameter vreg's
    // range starts at the block's first slot even though no instruction in
    // the block defines it.
    //
    //   bb0(%0:i8):
    //     %1 = pseudo.copy %0    ; slot 0 — use %0, def %1
    //
    // %0's def site is the block header (not an instruction); its range
    // starts at slot 0 (firstSlot of bb0) and ends at slot 0 (last use).
    [Test]
    public async Task BlockParameter_DefAtBlockEntry()
    {
        var fn = new MirFunction("blockparam", [IRType.I8], IRType.I8);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        bb0.Parameters.Add(v0);

        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false));

        var liveness = Analysis.Compute(fn);

        // Parameter is killed in its own block — not in LiveIn.
        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 0));
    }

    // Block-target arguments on a terminator count as vreg uses in the
    // terminator's block.
    //
    //   bb0(%0:i8):
    //     cf.br bb1(%0)          ; slot 0 — use %0 via block-target arg
    //   bb1(%1:i8):
    //     ; %1 is a block param of bb1
    //
    // %0 is used at slot 0 by the branch's BlockTarget.Args; LiveOut[bb0]
    // is empty because bb1's LiveIn doesn't include %0 (bb1's own parameter
    // %1 is what the arg flows into).
    [Test]
    public async Task BlockTargetArgs_CountAsUses()
    {
        var fn = new MirFunction("blockargs", [IRType.I8], IRType.I8);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        bb0.Parameters.Add(v0);
        bb1.Parameters.Add(v1);

        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br),
            new BlockTarget(bb1, [new VirtualReg(v0, IsDefinition: false)]));

        var liveness = Analysis.Compute(fn);

        // %0 used at slot 0 (the branch). Killed in bb0 (param), so not in
        // LiveOut.
        await Assert.That(liveness.LiveOut[bb0]).IsEmpty();
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 0));
    }

    // Vreg redefined in same instruction: the use is counted before the def,
    // so the vreg appears in upwardExposed even though it is also killed.
    //
    //   bb0:
    //     %0 = arith.constant 1            ; slot 0 — def %0
    //     %0 = arith.addi %0, %0           ; slot 1 — use %0, def %0
    //
    // Range covers both defs and the use: [0, 1].
    [Test]
    public async Task SelfRedefiningInstruction_UseCountedBeforeDef()
    {
        var fn = new MirFunction("redef", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, IsDefinition: true),
            new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, IsDefinition: true),
            new VirtualReg(v0, IsDefinition: false),
            new VirtualReg(v0, IsDefinition: false));

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 1));
    }
}
