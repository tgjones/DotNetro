namespace Irie.CodeGen.Passes;

public sealed class InstructionSelectorPass(InstructionSelector selector) : MachineFunctionPass
{
    public override string Name => "InstructionSelector";

    public override void Run(MachineFunction function)
    {
        selector.BeginFunction(function);
        var builder = new MachineIRBuilder(function);

        foreach (var block in function.Blocks)
        {
            // Snapshot the list so we can safely modify it during iteration.
            foreach (var instr in block.Instructions.ToList())
            {
                if (instr.Parent == null) continue; // removed by selector

                if (!selector.Select(instr, builder))
                    throw new InvalidOperationException(
                        $"InstructionSelector: no selection rule for opcode " +
                        $"{GenericOpcode.GetName(instr.Opcode) ?? instr.Opcode.ToString()}");
            }
        }

        // Once selection has succeeded, vreg types are no longer needed; class
        // assignments take over and the writer prints `%n:className` instead of `%n:type`.
        function.ClearVirtualRegisterTypes();
    }
}
