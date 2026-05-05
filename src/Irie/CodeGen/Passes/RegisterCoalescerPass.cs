namespace Irie.CodeGen.Passes;

// Coalesces virtual registers connected by GenericCopy instructions,
// eliminating the copy by merging src into dst.
//
// Runs after TwoAddressInstructionPass and before RegisterAllocatorPass.
//
// For each %dst = GenericCopy %src (both virtual):
//   - If register classes are compatible (same class or one is unset)
//   - If %src is killed at this copy (no uses after it in the block)
//   - If %src's definition is in the same block
//   Then: rewrite the definition of %src to produce %dst, RAUW %src→%dst
//         in instructions between the def and the copy, delete the copy.
//
// Physreg coalescing (vreg = GenericCopy $P) is left to the RA's copy-hints
// mechanism and CopyEliminationPass.
public sealed class RegisterCoalescerPass : MachineFunctionPass
{
    public override string Name => "RegisterCoalescer";

    public override void Run(MachineFunction function)
    {
        foreach (var block in function.Blocks)
            ProcessBlock(block, function);
    }

    private static void ProcessBlock(MachineBasicBlock block, MachineFunction function)
    {
        var i = 0;
        while (i < block.Instructions.Count)
        {
            var instr = block.Instructions[i];
            if (instr.Opcode == GenericOpcode.GenericCopy && TryCoalesce(block, i, instr, function))
                continue; // instruction removed; re-check same index
            i++;
        }
    }

    private static bool TryCoalesce(
        MachineBasicBlock block,
        int copyIdx,
        MachineInstruction copy,
        MachineFunction function)
    {
        if (copy.Operands.Length != 2) return false;
        if (copy.Operands[0] is not VirtualRegisterOperand { IsDefinition: true } dstOp) return false;
        if (copy.Operands[1] is not VirtualRegisterOperand { IsDefinition: false } srcOp) return false;

        var dst = dstOp.VirtualRegister;
        var src = srcOp.VirtualRegister;

        if (!TryGetMergedClass(function, src, dst, out var mergedClass)) return false;
        if (!IsKilledHere(block, copyIdx, src)) return false;

        var defInstr = FindDefinitionInBlock(block, src);
        if (defInstr == null) return false;

        // Rewrite the def: src → dst.
        for (var j = 0; j < defInstr.Operands.Length; j++)
        {
            if (defInstr.Operands[j] is VirtualRegisterOperand op &&
                op.VirtualRegister == src && op.IsDefinition)
                defInstr.Operands[j] = new VirtualRegisterOperand(dst, IsDefinition: true);
        }

        // RAUW uses of src with dst in all instructions before the copy.
        for (var j = 0; j < copyIdx; j++)
            ReplaceUses(block.Instructions[j], src, dst);

        if (mergedClass.HasValue)
            function.SetVirtualRegisterClass(dst, mergedClass.Value);

        block.Instructions.RemoveAt(copyIdx);
        return true;
    }

    private static bool TryGetMergedClass(
        MachineFunction fn, int src, int dst, out int? mergedClass)
    {
        var hasSrc = fn.TryGetVirtualRegisterClass(src, out var srcClass);
        var hasDst = fn.TryGetVirtualRegisterClass(dst, out var dstClass);

        if (!hasSrc && !hasDst) { mergedClass = null; return true; }
        if (!hasSrc)             { mergedClass = dstClass; return true; }
        if (!hasDst)             { mergedClass = srcClass; return true; }
        if (srcClass == dstClass){ mergedClass = srcClass; return true; }
        mergedClass = null;
        return false;
    }

    private static bool IsKilledHere(MachineBasicBlock block, int copyIdx, int src)
    {
        for (var i = copyIdx + 1; i < block.Instructions.Count; i++)
            foreach (var op in block.Instructions[i].Operands)
                if (op is VirtualRegisterOperand v && v.VirtualRegister == src && !v.IsDefinition)
                    return false;
        return true;
    }

    private static MachineInstruction? FindDefinitionInBlock(MachineBasicBlock block, int vreg)
    {
        foreach (var instr in block.Instructions)
            foreach (var op in instr.Operands)
                if (op is VirtualRegisterOperand v && v.VirtualRegister == vreg && v.IsDefinition)
                    return instr;
        return null;
    }

    private static void ReplaceUses(MachineInstruction instr, int oldVreg, int newVreg)
    {
        for (var j = 0; j < instr.Operands.Length; j++)
            if (instr.Operands[j] is VirtualRegisterOperand op &&
                op.VirtualRegister == oldVreg && !op.IsDefinition)
                instr.Operands[j] = new VirtualRegisterOperand(newVreg, IsDefinition: false);
    }
}
