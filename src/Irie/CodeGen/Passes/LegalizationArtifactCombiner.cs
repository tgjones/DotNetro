using Irie.IR;

namespace Irie.CodeGen.Passes;

// Folds redundant artifact pairs (Merge/Unmerge) introduced by legalization.
//
// The legalizer drives this in alternation with the per-instruction legalization
// step: legalizing a wide op typically inserts Unmerge/Merge pairs around the
// narrowed body, which this combiner then collapses by RAUW-ing through the
// original components.
public sealed class LegalizationArtifactCombiner(MachineFunction function)
{
    // Returns true if the instruction was combined. Adds any instructions
    // observed dead by the combine into deadInstrs (the caller erases them).
    public bool TryCombineInstruction(MachineInstruction instr, List<MachineInstruction> deadInstrs)
    {
        return instr.Opcode switch
        {
            GenericOpcode.GenericUnmerge => TryCombineUnmerge(instr, deadInstrs),
            _ => false,
        };
    }

    // Pattern:  Unmerge(Merge(p0..pN)) -> for each i: replace unmerge def_i with p_i
    //
    // Equal-arity, equal-element-type case only. The wider/narrower cases
    // (NumMergeRegs != NumDefs) are not handled yet.
    private bool TryCombineUnmerge(MachineInstruction unmerge, List<MachineInstruction> deadInstrs)
    {
        var defs = unmerge.Operands
            .OfType<VirtualRegisterOperand>()
            .Where(o => o.IsDefinition)
            .ToArray();
        var srcOperand = unmerge.Operands
            .OfType<VirtualRegisterOperand>()
            .First(o => !o.IsDefinition);

        var mergeInstr = function.GetDefinition(srcOperand.VirtualRegister);
        if (mergeInstr == null || mergeInstr.Opcode != GenericOpcode.GenericMerge)
            return false;

        var mergeSources = mergeInstr.Operands
            .OfType<VirtualRegisterOperand>()
            .Where(o => !o.IsDefinition)
            .ToArray();

        if (mergeSources.Length != defs.Length)
            return false;

        for (var i = 0; i < defs.Length; i++)
        {
            var defType = function.GetVirtualRegisterType(defs[i].VirtualRegister);
            var srcType = function.GetVirtualRegisterType(mergeSources[i].VirtualRegister);
            if (defType != srcType)
                return false;
        }

        for (var i = 0; i < defs.Length; i++)
        {
            function.ReplaceAllUsesOfRegister(
                oldVreg: defs[i].VirtualRegister,
                newVreg: mergeSources[i].VirtualRegister);
        }

        deadInstrs.Add(unmerge);

        // If the unmerge was the merge's only user, the merge becomes dead the
        // moment the unmerge is erased. Add it proactively — relying on the
        // bottom-up trivially-dead check is unreliable when the merge is added
        // to the worklist *after* its consumer (e.g. when legalization inserts
        // a fresh BuildMergeInto whose result feeds an existing unmerge that was
        // popped earlier).
        var mergeDef = mergeInstr.Operands
            .OfType<VirtualRegisterOperand>()
            .First(o => o.IsDefinition);
        if (function.GetUseCount(mergeDef.VirtualRegister) == 1)
            deadInstrs.Add(mergeInstr);

        return true;
    }
}
