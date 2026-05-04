namespace Irie.CodeGen.Passes;

// Resolves tied-operand constraints by inserting an explicit GenericCopy that
// unifies the tied use and the def into the same virtual register.
//
// For example:
//   %33:Ac, %34:Cc = ADC_ZeroPage %0:Ac(tied-def 0), %5:Imag8, %32:Cc
// becomes:
//   %33:Ac = GenericCopy %0:Ac
//   %33:Ac, %34:Cc = ADC_ZeroPage %33:Ac(tied-def 0), %5:Imag8, %32:Cc
//
// Run after PhiEliminationPass and before RegisterAllocatorPass.
// Breaks strict SSA (one def per vreg) intentionally.
public sealed class TwoAddressInstructionPass(Func<int, int[]?> tiedOperandsProvider)
    : MachineFunctionPass
{
    public override string Name => "TwoAddressInstruction";

    public override void Run(MachineFunction function)
    {
        foreach (var block in function.Blocks)
            ProcessBlock(block, function);
    }

    private void ProcessBlock(MachineBasicBlock block, MachineFunction function)
    {
        var i = 0;
        while (i < block.Instructions.Count)
        {
            var instr = block.Instructions[i];
            var tiedOperands = tiedOperandsProvider(instr.Opcode);
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
        MachineBasicBlock block,
        int instrIdx,
        MachineInstruction instr,
        MachineFunction function,
        int[] tiedOperands)
    {
        var inserted = 0;
        for (var usePos = 0; usePos < tiedOperands.Length; usePos++)
        {
            var defPos = tiedOperands[usePos];
            if (defPos < 0) continue;

            if (instr.Operands[defPos] is not VirtualRegisterOperand defOp || !defOp.IsDefinition)
                continue;

            if (instr.Operands[usePos] is not VirtualRegisterOperand useOp || useOp.IsDefinition)
                continue;

            var defVreg = defOp.VirtualRegister;
            var useVreg = useOp.VirtualRegister;

            if (defVreg == useVreg) continue;

            if (!function.TryGetVirtualRegisterClass(useVreg, out var classId))
                throw new InvalidOperationException(
                    $"TwoAddressInstructionPass: vreg %{useVreg} has no register class.");

            function.SetVirtualRegisterClass(defVreg, classId);

            block.InsertInstruction(
                instrIdx + inserted,
                GenericOpcode.GenericCopy,
                new VirtualRegisterOperand(defVreg, IsDefinition: true),
                new VirtualRegisterOperand(useVreg, IsDefinition: false));
            inserted++;

            instr.Operands[usePos] = new VirtualRegisterOperand(defVreg, IsDefinition: false);
        }
        return inserted;
    }
}
