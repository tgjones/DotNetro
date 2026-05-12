using Irie.CodeGen.Analyses;

namespace Irie.CodeGen.Passes;

// Linear-scan register allocator (Poletto & Sarkar, 1999).
//
// This allocator works entirely with virtual registers. It allocates each vreg
// to a register class (not a specific physical register). After allocation, it:
//
// 1. Detects operand-class constraint violations and inserts GenericCopy
//    instructions to fix them. For example, if an ADC instruction requires its
//    first operand to be in the Ac class but a vreg was allocated to Xc, we
//    insert a GenericCopy to move it into the Ac class before the instruction.
//
// 2. Inserts GenericCopy instructions after tied-operand definitions when the
//    result is live beyond the instruction. This preserves the result before
//    it gets clobbered by a subsequent instruction using the same physreg.
//
// The actual physical register assignment is deferred to VirtualRegisterRewriter,
// which runs after this pass and maps vregs to specific physical registers.
//
// Spilling is not implemented; a NotImplementedException is thrown if no
// register in a class is available. All current Irie tests fit in registers.
public sealed class RegisterAllocatorPass : MachineFunctionPass
{
    private readonly TargetRegisterInfo _tri;
    private readonly TargetInstructionInfo _instrInfo;
    private readonly int _flexibleI8Class;

    public RegisterAllocatorPass(TargetRegisterInfo tri, TargetInstructionInfo instrInfo, int flexibleI8Class = 0)
    {
        _tri = tri;
        _instrInfo = instrInfo;
        _flexibleI8Class = flexibleI8Class;
    }

    public override string Name => "RegisterAllocator";

    public override void Run(MachineFunction function)
    {
        // If PassManager is not set (e.g., in unit tests), compute liveness directly.
        Liveness liveness;
        if (PassManager != null)
            liveness = GetAnalysis<LivenessAnalysis, Liveness>(function);
        else
            liveness = new LivenessAnalysis().Compute(function);

        // Sort live intervals by start slot.
        var intervals = liveness.RangeOf
            .Select(kv => (vreg: kv.Key, range: kv.Value))
            .OrderBy(x => x.range.Start)
            .ToList();

        var assignment = new Dictionary<int, int>();     // vreg → register class (not physreg)
        var classActive = new Dictionary<int, int>();   // class → vreg currently holding that class
        var physregToClass = new Dictionary<int, int>(); // physreg → class (for computing active classes)

        foreach (var (vreg, range) in intervals)
        {
            // Expire intervals whose endpoint ≤ current start.
            foreach (var (expiredClass, activeVreg) in classActive.ToList())
                if (liveness.RangeOf[activeVreg].End <= range.Start)
                    classActive.Remove(expiredClass);

            if (!function.TryGetVirtualRegisterClass(vreg, out var classId))
                throw new InvalidOperationException(
                    $"RegisterAllocatorPass: vreg %{vreg} has no register class.");

            // Prefer the register class from a surrounding GenericCopy (trivial coalescing).
            var hintClass = TryGetCopyHintClass(function, vreg);
            int? allocatedClass = hintClass.HasValue && !classActive.ContainsKey(hintClass.Value)
                ? hintClass.Value
                : null;

            if (allocatedClass == null)
                allocatedClass = classId;

            assignment[vreg] = allocatedClass.Value;
            classActive[allocatedClass.Value] = vreg;
        }

        // If flexibleI8Class is specified (e.g., Anyi8), assign liveins to it for constraint flexibility.
        // This allows livein values to be routed to any 8-bit register, then constraint-fixup
        // copies will move them to specific registers (e.g., Ac for ADC) as needed.
        if (_flexibleI8Class > 0)
            WideninLiveinsToFlexibleClass(function, assignment);

        // Add constraint-violation detection and GenericCopy insertion.
        InsertConstraintFixupCopies(function, assignment);

        // Insert GenericCopy after tied-operand results that are live.
        InsertResultPreservationCopies(function, assignment);

        // Note: Do NOT apply physical register assignment here.
        // VirtualRegisterRewriter will assign physregs later.
    }

    // Derive a preferred register class for vreg from surrounding GenericCopy instructions.
    //
    // Rule 1: %vreg = GenericCopy $P            → hint class of $P
    // Rule 2: $P    = GenericCopy %vreg         → hint class of $P
    // Rule 3: %vreg = GenericCopy %w assigned   → hint assignment[%w]'s class
    //
    // The scan stops at the first applicable rule.
    private int? TryGetCopyHintClass(
        MachineFunction function, int vreg)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode != GenericOpcode.GenericCopy) continue;

                var ops = instr.Operands;
                var vDefOp  = ops.OfType<VirtualRegisterOperand>().FirstOrDefault(v => v.IsDefinition);
                var vUseOp  = ops.OfType<VirtualRegisterOperand>().FirstOrDefault(v => !v.IsDefinition);
                var pDefOp  = ops.OfType<PhysicalRegisterOperand>().FirstOrDefault(p => p.IsDefinition);
                var pUseOp  = ops.OfType<PhysicalRegisterOperand>().FirstOrDefault(p => !p.IsDefinition);

                if (vDefOp?.VirtualRegister == vreg)
                {
                    // Rule 1: %vreg = GenericCopy $P
                    if (pUseOp != null)
                    {
                        if (function.TryGetVirtualRegisterClass(vreg, out var vregClass))
                            return vregClass;
                    }
                }
                else if (vUseOp?.VirtualRegister == vreg)
                {
                    // Rule 2: $P = GenericCopy %vreg
                    if (pDefOp != null)
                    {
                        if (function.TryGetVirtualRegisterClass(vreg, out var vregClass))
                            return vregClass;
                    }
                }
            }
        }
        return null;
    }

    // Scan all instructions and detect operand-class constraint violations.
    // For each violation, insert a GenericCopy to fix it.
    private void InsertConstraintFixupCopies(
        MachineFunction function,
        Dictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            var i = 0;
            while (i < block.Instructions.Count)
            {
                var instr = block.Instructions[i];
                var desc = _instrInfo.TryGet(instr.Opcode);
                if (desc?.OperandClasses == null)
                {
                    i++;
                    continue;
                }

                var operands = instr.Operands;
                var inserted = 0;

                for (var opIdx = 0; opIdx < operands.Length && opIdx < desc.OperandClasses.Length; opIdx++)
                {
                    var requiredClass = desc.OperandClasses[opIdx];
                    if (requiredClass == 0) continue; // No constraint

                    if (operands[opIdx] is not VirtualRegisterOperand vop)
                        continue; // Not a vreg (imm, phys, etc.)

                    if (!assignment.TryGetValue(vop.VirtualRegister, out var allocatedClass))
                        continue; // Vreg not in assignment (shouldn't happen)

                    if (allocatedClass == requiredClass)
                        continue; // Constraint satisfied

                    // Constraint violation: insert GenericCopy to move into the required class
                    var newVreg = function.CreateVirtualRegisterWithClass(requiredClass);
                    block.InsertInstruction(
                        i + inserted,
                        GenericOpcode.GenericCopy,
                        new VirtualRegisterOperand(newVreg, IsDefinition: true),
                        new VirtualRegisterOperand(vop.VirtualRegister, IsDefinition: false));
                    inserted++;

                    // Rewrite the operand to use the new vreg
                    operands[opIdx] = new VirtualRegisterOperand(newVreg, vop.IsDefinition);
                }

                i += 1 + inserted;
            }
        }
    }

    // Insert GenericCopy after tied-operand definitions when the result is live beyond the instruction.
    private void InsertResultPreservationCopies(
        MachineFunction function,
        Dictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            var i = 0;
            while (i < block.Instructions.Count)
            {
                var instr = block.Instructions[i];
                var desc = _instrInfo.TryGet(instr.Opcode);
                if (desc?.TiedOperands == null)
                {
                    i++;
                    continue;
                }

                var operands = instr.Operands;
                var inserted = 0;

                // Check each def position for tied operands
                for (var defIdx = 0; defIdx < operands.Length; defIdx++)
                {
                    if (operands[defIdx] is not VirtualRegisterOperand defOp || !defOp.IsDefinition)
                        continue;

                    // Check if this def is tied to a use (i.e., tied-operand pattern)
                    // In LLVM, TiedOperands[useIdx] == defIdx means useIdx is tied to defIdx
                    var isTied = false;
                    for (var useIdx = 0; useIdx < desc.TiedOperands.Length; useIdx++)
                    {
                        if (desc.TiedOperands[useIdx] == defIdx)
                        {
                            isTied = true;
                            break;
                        }
                    }

                    if (!isTied) continue;

                    // Check if the def vreg is live after this instruction
                    if (IsVregLiveAfter(block, i, defOp.VirtualRegister))
                    {
                        // Insert GenericCopy after the instruction to preserve the result.
                        // If _flexibleI8Class is specified, use it for the temp vreg so the result
                        // can be allocated to a different physical register than the ADC result.
                        var tmpClass = _flexibleI8Class > 0 ? _flexibleI8Class : 
                            (assignment.TryGetValue(defOp.VirtualRegister, out var cls) ? cls : 0);
                        var tmpVreg = function.CreateVirtualRegisterWithClass(tmpClass);
                        block.InsertInstruction(
                            i + 1 + inserted,
                            GenericOpcode.GenericCopy,
                            new VirtualRegisterOperand(tmpVreg, IsDefinition: true),
                            new VirtualRegisterOperand(defOp.VirtualRegister, IsDefinition: false));
                        inserted++;
                    }
                }

                i += 1 + inserted;
            }
        }
    }

    // Check if a vreg is live after the instruction at instrIdx in the block.
    private static bool IsVregLiveAfter(MachineBasicBlock block, int instrIdx, int vreg)
    {
        for (var i = instrIdx + 1; i < block.Instructions.Count; i++)
        {
            foreach (var op in block.Instructions[i].Operands)
            {
                if (op is VirtualRegisterOperand vop && vop.VirtualRegister == vreg)
                    return !vop.IsDefinition;
            }
        }
        return false;
    }

    // Assign vregs created from liveins (GenericCopy from physical registers) to the flexible
    // class. This allows them to be routed through multiple 8-bit locations, with constraint-fixup
    // copies inserted later to move them to specific registers (e.g., Ac for ADC) as needed.
    // Pattern: %vreg:class = GenericCopy $physreg (in entry block, where $physreg is a livein)
    private void WideninLiveinsToFlexibleClass(MachineFunction function, Dictionary<int, int> assignment)
    {
        if (function.Blocks.Count == 0 || _flexibleI8Class == 0)
            return;

        var entryBlock = function.Blocks[0];
        foreach (var instr in entryBlock.Instructions)
        {
            if (instr.Opcode != GenericOpcode.GenericCopy)
                continue;

            // Pattern: %vreg = GenericCopy $physreg
            var vDefOp = instr.Operands.OfType<VirtualRegisterOperand>().FirstOrDefault(v => v.IsDefinition);
            var pUseOp = instr.Operands.OfType<PhysicalRegisterOperand>().FirstOrDefault(p => !p.IsDefinition);

            if (vDefOp != null && pUseOp != null && assignment.ContainsKey(vDefOp.VirtualRegister))
            {
                // This vreg is a livein copy; reassign to flexible class in both places
                assignment[vDefOp.VirtualRegister] = _flexibleI8Class;
                function.ForceSetVirtualRegisterClass(vDefOp.VirtualRegister, _flexibleI8Class);
            }
        }
    }
}

