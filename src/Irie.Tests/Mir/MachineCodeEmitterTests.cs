using Irie.MachineCode;
using Irie.Mir;
using Irie.Target.MOS6502;

namespace Irie.Tests.Mir;

public sealed class MachineCodeEmitterTests
{
    // Drives the emitter end-to-end on a tiny hand-built MirModule that skips
    // the parser. Two implied-mode instructions exercise the simplest path:
    // each MIR op produces one byte and zero operands. Anything more elaborate
    // is covered by the lit pipeline (notes/mir-to-machinecode-plan.md §4.1).
    [Test]
    public async Task Emit_ClcRts_ProducesExpectedMachineCode()
    {
        // Construct the target up front so MOS6502Dialect.Id is populated
        // before any OpRef captures it.
        var target = new MOS6502Target();

        var module = new MirModule();
        module.CreateFunction("F", [], IRType.Void, fn =>
        {
            var bb0 = fn.CreateBlock();
            bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Clc),
                new PhysicalReg(MOS6502Registers.C, IsDefinition: true));
            bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Rts));
        });

        var mc = target.MachineCodeEmitter.Emit(module);

        await Assert.That(mc.Functions).Count().IsEqualTo(1);
        var function = mc.Functions[0];
        await Assert.That(function.Name).IsEqualTo("F");
        await Assert.That(function.Body).Count().IsEqualTo(2);

        var clc = (MachineCodeInstruction)function.Body[0];
        await Assert.That(clc.Opcode).IsEqualTo(MOS6502Opcode.CLC);
        await Assert.That(clc.Operands).IsEmpty();

        var rts = (MachineCodeInstruction)function.Body[1];
        await Assert.That(rts.Opcode).IsEqualTo(MOS6502Opcode.RTS);
        await Assert.That(rts.Operands).IsEmpty();
    }
}
