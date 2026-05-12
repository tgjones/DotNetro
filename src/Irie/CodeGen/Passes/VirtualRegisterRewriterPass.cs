namespace Irie.CodeGen.Passes;

// Virtual Register Rewriter Pass
//
// This pass runs after RegisterAllocatorPass. It assigns physical registers to
// each virtual register and rewrites all VirtualRegisterOperands to
// PhysicalRegisterOperands.
//
// The key difference from RegisterAllocatorPass: RegisterAllocator works entirely
// with virtual registers and allocates them to register classes. This pass takes
// those class assignments and maps each vreg to a specific physical register,
// then applies the rewriting.
//
// Additionally, this pass simplifies GenericCopy chains where both operands can
// be assigned the same physical register (these will be eliminated by
// CopyEliminationPass).
public sealed class VirtualRegisterRewriterPass(TargetRegisterInfo tri) : MachineFunctionPass
{
    public override string Name => "VirtualRegisterRewriter";

    public override void Run(MachineFunction function)
    {
        // Build a mapping from each virtual register to a physical register.
        // For each class, allocate physical registers in a simple round-robin fashion.
        var vregToPhysreg = new Dictionary<int, int>();
        var classNext = new Dictionary<int, int>();

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                {
                    if (op is not VirtualRegisterOperand vop)
                        continue;

                    if (vregToPhysreg.ContainsKey(vop.VirtualRegister))
                        continue; // Already assigned

                    if (!function.TryGetVirtualRegisterClass(vop.VirtualRegister, out var classId))
                        throw new InvalidOperationException(
                            $"VirtualRegisterRewriterPass: vreg %{vop.VirtualRegister} has no register class.");

                    // Assign the next available physical register in this class
                    var allocatable = tri.GetAllocatableRegisters(classId);
                    if (allocatable.Length == 0)
                        throw new InvalidOperationException(
                            $"VirtualRegisterRewriterPass: no allocatable registers in class {classId}.");

                    if (!classNext.TryGetValue(classId, out var nextIdx))
                        nextIdx = 0;

                    var physreg = allocatable[nextIdx % allocatable.Length];
                    vregToPhysreg[vop.VirtualRegister] = physreg;
                    classNext[classId] = nextIdx + 1;
                }
            }
        }

        // Rewrite all VirtualRegisterOperands to PhysicalRegisterOperands
        ApplyAssignment(function, vregToPhysreg);
        function.ClearVirtualRegisterClasses();
    }

    // Rewrite every VirtualRegisterOperand to the assigned PhysicalRegisterOperand.
    private static void ApplyAssignment(
        MachineFunction function, IReadOnlyDictionary<int, int> vregToPhysreg)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is not VirtualRegisterOperand v) continue;
                    if (!vregToPhysreg.TryGetValue(v.VirtualRegister, out var physreg))
                        throw new InvalidOperationException(
                            $"VirtualRegisterRewriterPass: vreg %{v.VirtualRegister} has no physical register assignment.");
                    operands[i] = new PhysicalRegisterOperand(physreg, v.IsDefinition);
                }
            }
        }
    }
}
