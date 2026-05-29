using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Passes;

// Removes identity pseudo.copy instructions (those whose def and use are the
// same physical register, e.g. $a = pseudo.copy $a).
//
// These are left behind by RegisterAllocatorPass when copy hints assign the
// same physreg to both sides of a surrounding copy. Removing them is
// correctness-neutral but keeps the MIR clean for inspection and saves bytes
// at the CodeGen→MachineCode lowering stage.
//
// Runs after RegisterAllocatorPass — no virtual registers may remain.
public sealed class CopyEliminationPass : MirFunctionPass
{
    public override string Name => "CopyElimination";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
            block.Instructions.RemoveAll(IsIdentityCopy);
    }

    private static bool IsIdentityCopy(MirInstruction instr)
    {
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 2) return false;
        if (instr.Operands[0] is not PhysicalReg { IsDefinition: true } def) return false;
        if (instr.Operands[1] is not PhysicalReg { IsDefinition: false } use) return false;
        return def.Id == use.Id;
    }
}
