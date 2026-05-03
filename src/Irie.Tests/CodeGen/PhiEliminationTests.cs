using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.CodeGen;

public sealed class PhiEliminationTests
{
    private static readonly PhiEliminationPass Pass = new();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Build a two-block function whose bb1 has one parameter.
    //
    //   bb0():
    //     %0:Ac = LDImm1 0          ; some computation
    //     GenericJump bb1, %0       ; pass %0 as arg to bb1's %1 parameter
    //   bb1(%1:Ac):
    //     RTS_Implied
    //
    // After PhiElim:
    //   bb0():
    //     %0:Ac = LDImm1 0
    //     %1:Ac = GenericCopy %0    ; inserted before jump
    //     GenericJump bb1           ; arg stripped
    //   bb1():                      ; no parameters
    //     RTS_Implied
    private static MachineFunction BuildSingleParamFunction()
    {
        var fn = new MachineFunction("f");
        var bb0 = fn.CreateBasicBlock();
        var bb1 = fn.CreateBasicBlock();

        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.AddInstruction(MOS6502Opcode.LDImm1,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new ImmediateOperand(0));
        bb0.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1),
            new VirtualRegisterOperand(v0, IsDefinition: false));

        bb1.Parameters.Add(v1);
        bb1.AddInstruction(MOS6502Opcode.RTS, new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false, IsImplicit: true));

        return fn;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task NoBlockParams_FunctionUnchanged()
    {
        var fn = new MachineFunction("noop");
        var bb0 = fn.CreateBasicBlock();
        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        bb0.AddInstruction(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(v0, IsDefinition: true),
            new PhysicalRegisterOperand(MOS6502Registers.A, IsDefinition: false));
        bb0.AddInstruction(MOS6502Opcode.RTS);

        Pass.Run(fn);

        await Assert.That(fn.Blocks[0].Parameters.Count).IsEqualTo(0);
        await Assert.That(fn.Blocks[0].Instructions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SingleParam_CopyInsertedBeforeTerminator()
    {
        var fn = BuildSingleParamFunction();
        var (bb0, bb1) = (fn.Blocks[0], fn.Blocks[1]);
        var v1 = bb1.Parameters[0];

        Pass.Run(fn);

        // bb1 should have no parameters after the pass
        await Assert.That(bb1.Parameters.Count).IsEqualTo(0);

        // bb0 should now have 3 instructions: LDImm1, GenericCopy, GenericJump
        await Assert.That(bb0.Instructions.Count).IsEqualTo(3);

        var copy = bb0.Instructions[1];
        await Assert.That(copy.Opcode).IsEqualTo(GenericOpcode.GenericCopy);
        await Assert.That(copy.Operands[0]).IsTypeOf<VirtualRegisterOperand>();
        await Assert.That(((VirtualRegisterOperand)copy.Operands[0]).VirtualRegister).IsEqualTo(v1);
        await Assert.That(((VirtualRegisterOperand)copy.Operands[0]).IsDefinition).IsEqualTo(true);

        // Jump should have had its argument operand stripped
        var jump = bb0.Instructions[2];
        await Assert.That(jump.Opcode).IsEqualTo(GenericOpcode.GenericJump);
        await Assert.That(jump.Operands.Length).IsEqualTo(1);
        await Assert.That(jump.Operands[0]).IsTypeOf<BlockOperand>();
    }

    [Test]
    public async Task MultipleParams_AllCopiesInsertedInOrder()
    {
        var fn = new MachineFunction("multi");
        var bb0 = fn.CreateBasicBlock();
        var bb1 = fn.CreateBasicBlock();

        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var v1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var p0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var p1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1),
            new VirtualRegisterOperand(v0, IsDefinition: false),
            new VirtualRegisterOperand(v1, IsDefinition: false));

        bb1.Parameters.Add(p0);
        bb1.Parameters.Add(p1);
        bb1.AddInstruction(MOS6502Opcode.RTS);

        Pass.Run(fn);

        await Assert.That(bb1.Parameters.Count).IsEqualTo(0);
        // Two copies inserted before the jump
        await Assert.That(bb0.Instructions.Count).IsEqualTo(3);
        var copy0 = bb0.Instructions[0];
        var copy1 = bb0.Instructions[1];
        await Assert.That(copy0.Opcode).IsEqualTo(GenericOpcode.GenericCopy);
        await Assert.That(((VirtualRegisterOperand)copy0.Operands[0]).VirtualRegister).IsEqualTo(p0);
        await Assert.That(copy1.Opcode).IsEqualTo(GenericOpcode.GenericCopy);
        await Assert.That(((VirtualRegisterOperand)copy1.Operands[0]).VirtualRegister).IsEqualTo(p1);
        // Jump stripped of its args
        await Assert.That(bb0.Instructions[2].Operands.Length).IsEqualTo(1);
    }

    // A loop that swaps its two live variables:
    //
    //   bb0(%p0:Ac, %p1:Ac):
    //     GenericJump bb0, %p1, %p0    ; swap: new_p0 = p1, new_p1 = p0
    //
    // After PhiElim a temporary must break the cycle:
    //   temp = GenericCopy %p0   (or %p1, depending on which cycle entry is picked)
    //   %p1 = GenericCopy %p0   (or swap equivalent)
    //   %p0 = GenericCopy temp
    //   GenericJump bb0
    [Test]
    public async Task SwapCycle_BreaksCycleWithTemp()
    {
        var fn = new MachineFunction("swap");
        var bb0 = fn.CreateBasicBlock();

        var p0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var p1 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        bb0.Parameters.Add(p0);
        bb0.Parameters.Add(p1);
        bb0.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb0),
            new VirtualRegisterOperand(p1, IsDefinition: false),   // new p0 = p1
            new VirtualRegisterOperand(p0, IsDefinition: false));  // new p1 = p0

        Pass.Run(fn);

        await Assert.That(bb0.Parameters.Count).IsEqualTo(0);

        // Three copies inserted (temp save + two assignments), then the jump
        await Assert.That(bb0.Instructions.Count).IsEqualTo(4);

        // All three inserted instructions are GenericCopies
        for (var i = 0; i < 3; i++)
            await Assert.That(bb0.Instructions[i].Opcode).IsEqualTo(GenericOpcode.GenericCopy);

        // Jump has no args
        await Assert.That(bb0.Instructions[3].Operands.Length).IsEqualTo(1);

        // Simulate the sequential copies to verify they implement {p0 := p1, p1 := p0}.
        // State maps vreg id → "original owner" (each var starts holding itself).
        var state = new Dictionary<int, int> { [p0] = p0, [p1] = p1 };
        foreach (var instr in bb0.Instructions.Take(3))
        {
            var dst = ((VirtualRegisterOperand)instr.Operands[0]).VirtualRegister;
            var src = ((VirtualRegisterOperand)instr.Operands[1]).VirtualRegister;
            state[dst] = state.TryGetValue(src, out var orig) ? orig : src;
        }

        // After the swap, p0 must hold the original value of p1 and vice versa.
        await Assert.That(state[p0]).IsEqualTo(p1);
        await Assert.That(state[p1]).IsEqualTo(p0);
    }

    [Test]
    public void CriticalEdge_Throws()
    {
        // bb0 → bb1 and bb0 → bb2; bb1 also has another predecessor (bb2 → bb1).
        // bb1 has a parameter → critical edge from bb0 to bb1.
        var fn = new MachineFunction("crit");
        var bb0 = fn.CreateBasicBlock();
        var bb1 = fn.CreateBasicBlock();
        var bb2 = fn.CreateBasicBlock();

        var v0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);
        var p0 = fn.CreateVirtualRegisterWithClass(MOS6502RegisterClass.Ac);

        // bb0: conditional branch — two successors
        bb0.AddInstruction(GenericOpcode.GenericBranchConditional,
            new VirtualRegisterOperand(v0, IsDefinition: false),
            new BlockOperand(bb1),
            new VirtualRegisterOperand(p0, IsDefinition: false),   // arg for bb1
            new BlockOperand(bb2));
        // bb2: unconditional jump to bb1 — so bb1 has two predecessors
        bb2.AddInstruction(GenericOpcode.GenericJump,
            new BlockOperand(bb1),
            new VirtualRegisterOperand(p0, IsDefinition: false));

        bb1.Parameters.Add(p0);
        bb1.AddInstruction(MOS6502Opcode.RTS);

        Assert.Throws<NotImplementedException>(() => Pass.Run(fn));
    }
}
