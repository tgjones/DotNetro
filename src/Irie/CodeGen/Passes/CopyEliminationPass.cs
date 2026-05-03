namespace Irie.CodeGen.Passes;

// Removes identity GenericCopy instructions (those whose def and use are the
// same physical register, e.g. $A = GenericCopy $A).
//
// These are left behind by RegisterAllocatorPass when copy hints assign the
// same physreg to both sides of a surrounding copy.  Removing them is
// correctness-neutral but keeps the IR clean for inspection and saves bytes
// at the CodeGen→MachineCode lowering stage.
//
// Run after RegisterAllocatorPass (no virtual registers may remain).
public sealed class CopyEliminationPass : MachineFunctionPass
{
    public override string Name => "CopyElimination";

    public override void Run(MachineFunction function)
    {
        foreach (var block in function.Blocks)
            block.Instructions.RemoveAll(IsIdentityCopy);
    }

    private static bool IsIdentityCopy(MachineInstruction instr)
    {
        if (instr.Opcode != GenericOpcode.GenericCopy) return false;

        var ops = instr.Operands;
        var def = ops.OfType<PhysicalRegisterOperand>().FirstOrDefault(p => p.IsDefinition);
        var use = ops.OfType<PhysicalRegisterOperand>().FirstOrDefault(p => !p.IsDefinition);

        return def != null && use != null && def.Register == use.Register;
    }
}
