using Irie.CodeGen;
using Irie.CodeGen.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.CodeGen;

public sealed class LivenessAnalysisTests
{
    private static readonly LivenessAnalysis Analysis = new();

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    // Single block:
    //   %0 = GenericCopy $A    ; slot 0 — def %0
    //   %1 = GenericCopy %0    ; slot 1 — use %0, def %1
    //
    // No block successors so LiveIn/LiveOut are both empty.
    // %0 is live [0, 1]; %1 is defined at slot 1 and has no uses (dead), range [1, 1].
    [Test]
    public async Task SingleBlock_RangesReflectDefAndLastUse()
    {
        var fn = new MachineFunction("single");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        var i0 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        var i1 = bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v1, IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));


        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEmpty();

        await Assert.That(liveness.SlotOf[i0]).IsEqualTo(0);
        await Assert.That(liveness.SlotOf[i1]).IsEqualTo(1);

        // %0: defined at slot 0, last used at slot 1
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 1));
        // %1: defined at slot 1, no uses — dead, but range covers the def
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(1, 1));
    }

    // Two sequential blocks — %0 defined in bb0, used in bb1:
    //
    //   bb0:
    //     %0 = GenericCopy $A    ; slot 0 — def %0
    //     GenericJump bb1        ; slot 1
    //   bb1:
    //     %1 = GenericCopy %0    ; slot 2 — use %0, def %1
    //
    // Dataflow: LiveIn[bb1] = {%0}, LiveOut[bb0] = {%0}.
    // %0 range is widened by LiveOut[bb0] to slot 1 and by LiveIn[bb1] to slot 2:
    //   range [0, 2] (def at 0, end of bb0 at 1, use in bb1 at 2).
    [Test]
    public async Task TwoBlocks_CrossBlockRangeWidened()
    {
        var fn = new MachineFunction("two");
        var bb0 = fn.CreateBasicBlock();
        var bb1 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1));

        bb1.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v1, IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEquivalentTo(new[] { v0 });

        await Assert.That(liveness.LiveIn[bb1]).IsEquivalentTo(new[] { v0 });
        await Assert.That(liveness.LiveOut[bb1]).IsEmpty();

        // %0: def at slot 0; widened to lastSlot(bb0)=1 by LiveOut; widened to
        //     firstSlot(bb1)=2 by LiveIn; used at slot 2 — range [0, 2].
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 2));
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(2, 2));
    }

    // Self-loop — tests fixed-point convergence:
    //
    //   bb0:
    //     %0 = GenericCopy $A    ; slot 0 — def %0
    //     GenericJump bb1        ; slot 1
    //   bb1:
    //     %1 = GenericCopy %0    ; slot 2 — use %0, def %1
    //     GenericJump bb1        ; slot 3 (self-loop)
    //
    // LiveIn[bb1] = {%0} (upward-exposed), LiveOut[bb1] = {%0} (from self-loop).
    // %0 range covers bb0 + entire bb1 body: [0, 3].
    [Test]
    public async Task SelfLoop_LivenessConvergesAndRangeCoversFull()
    {
        var fn = new MachineFunction("loop");
        var bb0 = fn.CreateBasicBlock();
        var bb1 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1));

        bb1.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v1, IsDefinition: true),
            new VirtualRegisterOperand(v0, IsDefinition: false));
        bb1.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1));  // self-loop

        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.LiveIn[bb0]).IsEmpty();
        await Assert.That(liveness.LiveOut[bb0]).IsEquivalentTo(new[] { v0 });

        await Assert.That(liveness.LiveIn[bb1]).IsEquivalentTo(new[] { v0 });
        // bb1 loops to itself; LiveOut[bb1] = LiveIn[bb1] = {%0}
        await Assert.That(liveness.LiveOut[bb1]).IsEquivalentTo(new[] { v0 });

        // %0: widened by LiveOut[bb0]→slot 1, LiveIn[bb1]→slot 2, use→slot 2, LiveOut[bb1]→slot 3
        await Assert.That(liveness.RangeOf[v0]).IsEqualTo(new LiveRange(0, 3));
        // %1: defined at slot 2, not in LiveOut[bb1] — range [2, 2]
        await Assert.That(liveness.RangeOf[v1]).IsEqualTo(new LiveRange(2, 2));
    }

    // Empty function (no blocks) — should not throw, should return empty liveness.
    [Test]
    public async Task EmptyFunction_NoRanges()
    {
        var fn = new MachineFunction("empty");
        var liveness = Analysis.Compute(fn);

        await Assert.That(liveness.RangeOf.Count).IsEqualTo(0);
        await Assert.That(liveness.SlotOf.Count).IsEqualTo(0);
    }
}
