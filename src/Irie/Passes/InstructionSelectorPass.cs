using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Walks each function's instructions in order, asking the target's selector
// to lower each one. Mirrors the old Irie.CodeGen.Passes.InstructionSelectorPass.
//
// Per the unified-IR plan, selection commits each vreg to a register class:
// new defs are created via CreateVirtualRegisterInClass and existing vreg uses
// are converted from TypedVReg to ClassedVReg via ReclassifyVirtualRegister.
public sealed class InstructionSelectorPass(Irie.Target.InstructionSelector selector) : MirFunctionPass
{
    public override string Name => "InstructionSelector";

    public override void Run(MirFunction function)
    {
        selector.BeginFunction(function);
        var builder = new MirBuilder(function);

        foreach (var block in function.Blocks)
        {
            // Snapshot the list so we can safely modify it during iteration.
            foreach (var instr in block.Instructions.ToList())
            {
                if (instr.Parent == null) continue; // removed by selector

                if (!selector.Select(instr, builder))
                {
                    var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
                    throw new InvalidOperationException(
                        $"InstructionSelector: no selection rule for opcode " +
                        $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}");
                }
            }
        }
    }
}
