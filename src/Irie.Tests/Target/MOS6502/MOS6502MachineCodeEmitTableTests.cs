using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

public sealed class MOS6502MachineCodeEmitTableTests
{
    // The seven entries the current pipeline actually produces, with the
    // opcode byte and operand interpretation each MIR op lowers to. Mirrors
    // the table in notes/mir-to-machinecode-plan.md §2.2.
    // expectedOperandIndex == -1 stands in for null; attribute arguments
    // can't carry a Nullable<int> directly.
    [Test]
    [Arguments(MOS6502Op.Clc,   MOS6502Opcode.CLC,          EmitOperandKind.Implied,         -1)]
    [Arguments(MOS6502Op.AdcZp, MOS6502Opcode.ADC_ZeroPage, EmitOperandKind.ZeroPageAddress,  3)]
    [Arguments(MOS6502Op.StaZp, MOS6502Opcode.STA_ZeroPage, EmitOperandKind.ZeroPageAddress,  0)]
    [Arguments(MOS6502Op.LdaZp, MOS6502Opcode.LDA_ZeroPage, EmitOperandKind.ZeroPageAddress,  1)]
    [Arguments(MOS6502Op.LdxZp, MOS6502Opcode.LDX_ZeroPage, EmitOperandKind.ZeroPageAddress,  1)]
    [Arguments(MOS6502Op.Txa,   MOS6502Opcode.TXA,          EmitOperandKind.Implied,         -1)]
    [Arguments(MOS6502Op.Rts,   MOS6502Opcode.RTS,          EmitOperandKind.Implied,         -1)]
    public async Task Get_ReturnsExpectedRule(
        MOS6502Op       op,
        int             expectedOpcodeByte,
        EmitOperandKind expectedKind,
        int             expectedOperandIndex)
    {
        var rule = MOS6502MachineCodeEmitTable.Get(op);
        await Assert.That(rule.OpcodeByte).IsEqualTo(expectedOpcodeByte);
        await Assert.That(rule.Kind).IsEqualTo(expectedKind);
        await Assert.That(rule.OperandIndex).IsEqualTo(expectedOperandIndex == -1 ? (int?)null : expectedOperandIndex);
    }

    // The table is intentionally incomplete — pre-AMS opcodes like `Cmp`
    // should never reach the emitter (AMS refines them to a post-AMS form
    // first). Asking for one is a bug in an upstream pass, so the table
    // throws loudly rather than silently emitting garbage.
    [Test]
    public async Task Get_ThrowsForUnmappedOpcode()
    {
        await Assert.That(() => MOS6502MachineCodeEmitTable.Get(MOS6502Op.Cmp))
            .Throws<KeyNotFoundException>();
    }
}
