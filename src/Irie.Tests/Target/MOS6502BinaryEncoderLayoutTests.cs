using Irie.MachineCode;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target;

public sealed class MOS6502BinaryEncoderLayoutTests
{
    // CLC (1) + LDA #$00 (2) + JMP $1234 (3) at origin 0x1000 should lay out at
    // 0x1000, 0x1001, 0x1003 with the function address pinned to the origin.
    [Test]
    public async Task LayoutOnly_MixedAddressingModes_AssignsExpectedAddresses()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.CLC);
        function.EmitInstruction(MOS6502Opcode.LDA_Immediate, new MachineCodeOperand.Immediate(0x00));
        function.EmitInstruction(MOS6502Opcode.JMP_Absolute,  new MachineCodeOperand.Immediate(0x1234));

        var layout = new MOS6502BinaryEncoder().LayoutOnly(module, 0x1000);

        await Assert.That(layout.FunctionAddrs["F"]).IsEqualTo(0x1000);
        await Assert.That(layout.Entries).Count().IsEqualTo(3);
        await Assert.That(layout.Entries[0].Addr).IsEqualTo(0x1000);
        await Assert.That(layout.Entries[0].Parent).IsEqualTo(function);
        await Assert.That(layout.Entries[0].Instruction.Opcode).IsEqualTo(MOS6502Opcode.CLC);
        await Assert.That(layout.Entries[1].Addr).IsEqualTo(0x1001);
        await Assert.That(layout.Entries[1].Instruction.Opcode).IsEqualTo(MOS6502Opcode.LDA_Immediate);
        await Assert.That(layout.Entries[2].Addr).IsEqualTo(0x1003);
        await Assert.That(layout.Entries[2].Instruction.Opcode).IsEqualTo(MOS6502Opcode.JMP_Absolute);
    }

    // Labels are zero-width: a label between two CLCs sits at the same address
    // as the following instruction.
    [Test]
    public async Task LayoutOnly_LabelBetweenInstructions_LabelAddressEqualsFollowingInstruction()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.CLC);
        function.EmitLabel("loop");
        function.EmitInstruction(MOS6502Opcode.CLC);

        var layout = new MOS6502BinaryEncoder().LayoutOnly(module, 0x2000);

        await Assert.That(layout.FunctionAddrs["F"]).IsEqualTo(0x2000);
        await Assert.That(layout.LocalLabelAddrs[function]["loop"]).IsEqualTo(0x2001);
        await Assert.That(layout.Entries).Count().IsEqualTo(2);
        await Assert.That(layout.Entries[0].Addr).IsEqualTo(0x2000);
        await Assert.That(layout.Entries[1].Addr).IsEqualTo(0x2001);
    }

    // Two back-to-back functions: the second one starts immediately after the
    // first's instructions end.
    [Test]
    public async Task LayoutOnly_TwoFunctions_SecondStartsAfterFirst()
    {
        var module = new MachineCodeModule();

        var first = module.CreateFunction("First");
        first.EmitInstruction(MOS6502Opcode.JSR_Absolute, new MachineCodeOperand.ExternalRef("Second"));
        first.EmitInstruction(MOS6502Opcode.RTS);

        var second = module.CreateFunction("Second");
        second.EmitInstruction(MOS6502Opcode.RTS);

        var layout = new MOS6502BinaryEncoder().LayoutOnly(module, 0x1000);

        // First: JSR (3) + RTS (1) = 4 bytes; Second starts at 0x1004.
        await Assert.That(layout.FunctionAddrs["First"]).IsEqualTo(0x1000);
        await Assert.That(layout.FunctionAddrs["Second"]).IsEqualTo(0x1004);
        await Assert.That(layout.Entries).Count().IsEqualTo(3);
        await Assert.That(layout.Entries[0].Addr).IsEqualTo(0x1000);
        await Assert.That(layout.Entries[0].Parent).IsEqualTo(first);
        await Assert.That(layout.Entries[1].Addr).IsEqualTo(0x1003);
        await Assert.That(layout.Entries[1].Parent).IsEqualTo(first);
        await Assert.That(layout.Entries[2].Addr).IsEqualTo(0x1004);
        await Assert.That(layout.Entries[2].Parent).IsEqualTo(second);
    }

    // Pass 2 is intentionally not yet implemented; Encode() should fail loudly
    // until step 3 of the plan lands.
    [Test]
    public async Task Encode_ThrowsNotImplemented()
    {
        var module = new MachineCodeModule();
        module.CreateFunction("F").EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<NotImplementedException>()
            .WithMessageContaining("pass 2");
    }

    [Test]
    public async Task LayoutOnly_DuplicateFunctionName_Throws()
    {
        var module = new MachineCodeModule();
        module.CreateFunction("F").EmitInstruction(MOS6502Opcode.RTS);
        module.CreateFunction("F").EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().LayoutOnly(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("duplicate function name");
    }

    [Test]
    public async Task LayoutOnly_DuplicateLabelWithinFunction_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitLabel("loop");
        function.EmitInstruction(MOS6502Opcode.CLC);
        function.EmitLabel("loop");

        await Assert.That(() => new MOS6502BinaryEncoder().LayoutOnly(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("duplicate label");
    }
}
