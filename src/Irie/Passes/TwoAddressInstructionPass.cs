using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Passes;

// Resolves tied-operand constraints by inserting an explicit pseudo.copy that
// unifies the tied use and the def into the same virtual register.
//
// For example:
//   %33 : Ac, %34 : Cc = mos6502.adc %0(tied-def 0), %5, %32
// becomes:
//   %33 : Ac = pseudo.copy %0
//   %33 : Ac, %34 : Cc = mos6502.adc %33(tied-def 0), %5, %32
//
// Runs after PhiEliminationPass and before RegisterAllocatorPass.
// Breaks strict SSA (one def per vreg) intentionally — the inserted copy and
// the original instruction now both define the same tied-def vreg, which is
// exactly the shape RA needs to assign a single physreg to both ends.
//
// Tied-operand metadata comes from the dialect's GetInstructionInfo. Each
// element of TiedOperands is the def index that operand is tied to, or -1.
public sealed class TwoAddressInstructionPass : MirFunctionPass
{
    public override string Name => "TwoAddressInstruction";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
            ProcessBlock(block, function);
    }

    private static void ProcessBlock(MirBlock block, MirFunction function)
    {
        var i = 0;
        while (i < block.Instructions.Count)
        {
            var instr = block.Instructions[i];
            var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
            var tiedOperands = dialect.GetInstructionInfo(instr.Opcode.Code).TiedOperands;
            if (tiedOperands != null)
            {
                var inserted = ProcessInstruction(block, i, instr, function, tiedOperands);
                i += inserted;
            }
            i++;
        }
    }

    // Returns the number of copy instructions inserted before instrIdx.
    private static int ProcessInstruction(
        MirBlock block,
        int instrIdx,
        MirInstruction instr,
        MirFunction function,
        int[] tiedOperands)
    {
        var inserted = 0;
        for (var usePos = 0; usePos < tiedOperands.Length; usePos++)
        {
            var defPos = tiedOperands[usePos];
            if (defPos < 0) continue;

            if (instr.Operands[defPos] is not VirtualReg defOp || !defOp.IsDefinition)
                continue;

            if (instr.Operands[usePos] is not VirtualReg useOp || useOp.IsDefinition)
                continue;

            var defVreg = defOp.Id;
            var useVreg = useOp.Id;

            if (defVreg == useVreg) continue;

            if (function.GetVRegAnnotation(useVreg) is not ClassedVReg classed)
                throw new InvalidOperationException(
                    $"TwoAddressInstructionPass: vreg %{useVreg} has no register class.");

            function.ReclassifyVirtualRegister(defVreg, classed.ClassId, classed.Name);

            block.InsertInstruction(
                instrIdx + inserted,
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(defVreg, IsDefinition: true),
                new VirtualReg(useVreg, IsDefinition: false));
            inserted++;

            instr.Operands[usePos] = new VirtualReg(defVreg, IsDefinition: false);
        }
        return inserted;
    }
}
