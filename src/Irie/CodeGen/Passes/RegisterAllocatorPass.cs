using Irie.CodeGen.Analyses;

namespace Irie.CodeGen.Passes;

// Linear-scan register allocator (Poletto & Sarkar, 1999).
//
// Two design decisions that differ from the original paper — both required for
// the IntegerAdd32 case to allocate without spilling:
//
// 1. Copy hints override class constraints.
//    When a vreg has a copy hint to physreg P (derived from a surrounding
//    GenericCopy), assign P even if P is outside the vreg's register class.
//    GenericCopy is a free move in MIR; class constraints are advisory until
//    the CodeGen→MachineCode lowering pass, which can insert TXA/LDA/etc. to
//    satisfy tied-operand requirements on real instructions.
//
// 2. Expiry rule is endpoint ≤ currentStart (not <).
//    The tied-operand pattern (e.g. ADC reads and writes $A at the same slot)
//    requires the read vreg to expire before the write vreg is allocated.
//    Using < would leave $A appearing occupied → spurious spill.
//
// Spilling is not implemented; a NotImplementedException is thrown if no
// register is available. All current Irie tests fit in registers.
public sealed class RegisterAllocatorPass(TargetRegisterInfo tri) : MachineFunctionPass
{
    public override string Name => "RegisterAllocator";

    public override void Run(MachineFunction function)
    {
        var liveness = GetAnalysis<LivenessAnalysis, Liveness>(function);

        // Sort live intervals by start slot.
        var intervals = liveness.RangeOf
            .Select(kv => (vreg: kv.Key, range: kv.Value))
            .OrderBy(x => x.range.Start)
            .ToList();

        var assignment = new Dictionary<int, int>();    // vreg → physreg
        var physregActive = new Dictionary<int, int>(); // physreg → vreg currently assigned

        foreach (var (vreg, range) in intervals)
        {
            // Expire intervals whose endpoint ≤ current start (see design note 2).
            foreach (var (physreg, activeVreg) in physregActive.ToList())
                if (liveness.RangeOf[activeVreg].End <= range.Start)
                    physregActive.Remove(physreg);

            if (!function.TryGetVirtualRegisterClass(vreg, out var classId))
                throw new InvalidOperationException(
                    $"RegisterAllocatorPass: vreg %{vreg} has no register class.");

            // Prefer the physreg from a surrounding GenericCopy (trivial coalescing).
            // The hint wins even when it is outside the class (see design note 1).
            var hint = TryGetCopyHint(function, vreg, assignment);
            int? allocated = hint.HasValue && !physregActive.ContainsKey(hint.Value)
                ? hint.Value
                : null;

            if (allocated == null)
            {
                foreach (var physreg in tri.GetAllocatableRegisters(classId))
                {
                    if (!physregActive.ContainsKey(physreg))
                    {
                        allocated = physreg;
                        break;
                    }
                }
            }

            if (allocated == null)
                throw new NotImplementedException(
                    $"RegisterAllocatorPass: spilling not yet implemented " +
                    $"(vreg %{vreg}, class {classId}).");

            assignment[vreg] = allocated.Value;
            physregActive[allocated.Value] = vreg;
        }

        ApplyAssignment(function, assignment);
        function.ClearVirtualRegisterClasses();
    }

    // Derive a preferred physreg for vreg from surrounding GenericCopy instructions.
    //
    // Rule 1: %vreg = GenericCopy $P              → hint $P
    // Rule 2: $P    = GenericCopy %vreg           → hint $P
    // Rule 3: %vreg = GenericCopy %w, %w assigned → hint assignment[%w]
    // Rule 4: %w    = GenericCopy %vreg, %w assigned → hint assignment[%w]
    //
    // Rules 3 & 4 propagate assignments transitively through vreg-to-vreg copies.
    // The scan stops at the first applicable rule.
    private static int? TryGetCopyHint(
        MachineFunction function, int vreg, IReadOnlyDictionary<int, int> assignment)
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
                    if (pUseOp != null) return pUseOp.Register;
                    // Rule 3: %vreg = GenericCopy %w, %w already assigned
                    if (vUseOp != null && assignment.TryGetValue(vUseOp.VirtualRegister, out var r3))
                        return r3;
                }
                else if (vUseOp?.VirtualRegister == vreg)
                {
                    // Rule 2: $P = GenericCopy %vreg
                    if (pDefOp != null) return pDefOp.Register;
                    // Rule 4: %w = GenericCopy %vreg, %w already assigned
                    if (vDefOp != null && assignment.TryGetValue(vDefOp.VirtualRegister, out var r4))
                        return r4;
                }
            }
        }
        return null;
    }

    // Rewrite every VirtualRegisterOperand to the assigned PhysicalRegisterOperand.
    private static void ApplyAssignment(
        MachineFunction function, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is not VirtualRegisterOperand v) continue;
                    if (!assignment.TryGetValue(v.VirtualRegister, out var physreg))
                        throw new InvalidOperationException(
                            $"RegisterAllocatorPass: vreg %{v.VirtualRegister} has no assignment.");
                    operands[i] = new PhysicalRegisterOperand(physreg, v.IsDefinition);
                }
            }
        }
    }
}
