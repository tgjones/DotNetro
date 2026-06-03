using Irie.MachineCode;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target;

public sealed class MOS6502BinaryEncoderTests
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

    // Smallest possible program: a single RTS encodes to one byte.
    [Test]
    public async Task Encode_SingleRts_ReturnsOneByte()
    {
        var module = new MachineCodeModule();
        module.CreateFunction("F").EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x60 });
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

    // Implied-only opcodes encode to their single-byte opcode value with no operand.
    [Test]
    public async Task Encode_ImpliedOnly_EncodesOneByteEach()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.CLC);
        function.EmitInstruction(MOS6502Opcode.SEC);
        function.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x18, 0x38, 0x60 });
    }

    // Mixed immediate / zero-page / implied — each instruction takes its
    // declared length and the operand bytes follow the opcode byte.
    [Test]
    public async Task Encode_ImmediateZeroPageAndImpliedTransfer_EncodesExpectedBytes()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.LDA_Immediate, new MachineCodeOperand.Immediate(0x42));
        function.EmitInstruction(MOS6502Opcode.STA_ZeroPage,  new MachineCodeOperand.Immediate(0x05));
        function.EmitInstruction(MOS6502Opcode.TXA);
        function.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xA9, 0x42, 0x85, 0x05, 0x8A, 0x60 });
    }

    // Absolute mode with an Immediate operand carries the literal address LE.
    // JSR $FFEE is the canonical OSWRCH-style call.
    [Test]
    public async Task Encode_AbsoluteImmediateOperand_EncodesLittleEndian()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.JSR_Absolute, new MachineCodeOperand.Immediate(0xFFEE));
        function.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x20, 0xEE, 0xFF, 0x60 });
    }

    // A local label resolves to an absolute address for JMP and to a signed
    // 8-bit offset for BEQ. The BEQ here is a backward branch.
    [Test]
    public async Task Encode_LocalLabelAbsoluteAndRelative_BackwardBranch()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        // 0x1000: loop:
        function.EmitLabel("loop");
        // 0x1000: NOP (1) — at the label
        function.EmitInstruction(MOS6502Opcode.NOP);
        // 0x1001: BEQ loop — relative; target=0x1000, PC=0x1003, offset=-3 → 0xFD
        function.EmitInstruction(MOS6502Opcode.BEQ, new MachineCodeOperand.LabelRef("loop"));
        // 0x1003: JMP loop — absolute; encodes LE 16-bit 0x1000
        function.EmitInstruction(MOS6502Opcode.JMP_Absolute, new MachineCodeOperand.LabelRef("loop"));
        // 0x1006: RTS
        function.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[]
        {
            0xEA,             // NOP
            0xF0, 0xFD,       // BEQ -3
            0x4C, 0x00, 0x10, // JMP $1000
            0x60,             // RTS
        });
    }

    // Forward branch over a small block — positive offset, computed from PC
    // (the address after the branch instruction).
    [Test]
    public async Task Encode_RelativeForwardBranch_PositiveOffset()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        // 0x1000: BEQ skip — target=0x1004, PC=0x1002, offset=+2
        function.EmitInstruction(MOS6502Opcode.BEQ, new MachineCodeOperand.LabelRef("skip"));
        // 0x1002: NOP, NOP
        function.EmitInstruction(MOS6502Opcode.NOP);
        function.EmitInstruction(MOS6502Opcode.NOP);
        // 0x1004: skip:
        function.EmitLabel("skip");
        function.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xF0, 0x02, 0xEA, 0xEA, 0x60 });
    }

    // Two functions; the first JSRs the second by symbol. The JSR operand
    // should resolve to the helper's absolute address LE-encoded.
    [Test]
    public async Task Encode_CrossFunctionJsr_ResolvesExternalRef()
    {
        var module = new MachineCodeModule();

        // 0x1000..0x1003: caller (JSR=3, RTS=1)
        var caller = module.CreateFunction("Caller");
        caller.EmitInstruction(MOS6502Opcode.JSR_Absolute, new MachineCodeOperand.ExternalRef("helper"));
        caller.EmitInstruction(MOS6502Opcode.RTS);

        // 0x1004: helper (RTS)
        var helper = module.CreateFunction("helper");
        helper.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        // JSR helper → JSR $1004; LE = 04 10
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x20, 0x04, 0x10, 0x60, 0x60 });
    }

    // SymbolHalf.LowByte / HighByte resolve to the matching half of a function
    // address as a single immediate byte.
    [Test]
    public async Task Encode_ExternalRefSymbolHalf_LowAndHighBytes()
    {
        var module = new MachineCodeModule();

        // sym occupies the low byte slot we want: pad the first function to land at 0x12C4.
        // Origin 0x1000, first function = 0x2C4 bytes of NOPs, then sym starts at 0x12C4.
        var caller = module.CreateFunction("Caller");
        caller.EmitInstruction(MOS6502Opcode.LDA_Immediate,
            new MachineCodeOperand.ExternalRef("sym", SymbolHalf.LowByte));
        caller.EmitInstruction(MOS6502Opcode.LDA_Immediate,
            new MachineCodeOperand.ExternalRef("sym", SymbolHalf.HighByte));
        // Caller is 4 bytes (LDA #lo + LDA #hi). Pad with NOPs to push sym out to 0x12C4.
        // Caller starts at 0x1000 and is 4 bytes, sym should start at 0x12C4,
        // so pad 0x12C4 - 0x1004 = 0x2C0 bytes.
        for (var i = 0; i < 0x2C0; i++)
            caller.EmitInstruction(MOS6502Opcode.NOP);

        var sym = module.CreateFunction("sym");
        sym.EmitInstruction(MOS6502Opcode.RTS);

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        // bytes[0..1] = LDA #<sym = A9 C4; bytes[2..3] = LDA #>sym = A9 12
        await Assert.That(bytes[0]).IsEqualTo((byte)0xA9);
        await Assert.That(bytes[1]).IsEqualTo((byte)0xC4);
        await Assert.That(bytes[2]).IsEqualTo((byte)0xA9);
        await Assert.That(bytes[3]).IsEqualTo((byte)0x12);
    }

    // Pad with enough NOPs that the BEQ target is more than 127 bytes ahead.
    [Test]
    public async Task Encode_BranchOutOfRange_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.BEQ, new MachineCodeOperand.LabelRef("far"));
        // 200 NOPs ≫ 127 bytes between BEQ and 'far'.
        for (var i = 0; i < 200; i++)
            function.EmitInstruction(MOS6502Opcode.NOP);
        function.EmitLabel("far");
        function.EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("out of range");
    }

    [Test]
    public async Task Encode_UndefinedSymbol_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.JSR_Absolute,
            new MachineCodeOperand.ExternalRef("nonexistent"));
        function.EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("nonexistent");
    }

    // SymbolHalf only makes sense with Immediate addressing; using it on an
    // Absolute operand is a hard error.
    [Test]
    public async Task Encode_SymbolHalfOnAbsoluteOperand_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.JSR_Absolute,
            new MachineCodeOperand.ExternalRef("F", SymbolHalf.LowByte));
        function.EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("LowByte");
    }

    [Test]
    public async Task Encode_UndefinedLocalLabel_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitInstruction(MOS6502Opcode.BEQ, new MachineCodeOperand.LabelRef("missing"));
        function.EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("missing");
    }

    // LDA-immediate expects Immediate or SymbolHalf operand, not LabelRef.
    [Test]
    public async Task Encode_WrongOperandVariantForAddressingMode_Throws()
    {
        var module = new MachineCodeModule();
        var function = module.CreateFunction("F");
        function.EmitLabel("here");
        function.EmitInstruction(MOS6502Opcode.LDA_Immediate, new MachineCodeOperand.LabelRef("here"));
        function.EmitInstruction(MOS6502Opcode.RTS);

        await Assert.That(() => new MOS6502BinaryEncoder().Encode(module, 0x1000))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("LabelRef");
    }

    // Total length = sum of instruction lengths across all functions.
    [Test]
    public async Task Encode_TotalLength_EqualsSumOfInstructionLengths()
    {
        var module = new MachineCodeModule();
        var a = module.CreateFunction("A");
        a.EmitInstruction(MOS6502Opcode.CLC);                                                 // 1
        a.EmitInstruction(MOS6502Opcode.LDA_Immediate, new MachineCodeOperand.Immediate(1));  // 2
        a.EmitInstruction(MOS6502Opcode.JMP_Absolute,  new MachineCodeOperand.Immediate(0));  // 3
        var b = module.CreateFunction("B");
        b.EmitInstruction(MOS6502Opcode.RTS);                                                 // 1

        var bytes = new MOS6502BinaryEncoder().Encode(module, 0x1000);

        await Assert.That(bytes.Length).IsEqualTo(1 + 2 + 3 + 1);
    }
}
