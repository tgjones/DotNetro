using Irie.IR;

namespace Irie.CodeGen;

public sealed class MachineFunction(string name)
{
    public string Name => name;

    public List<MachineBasicBlock> Blocks { get; } = [];

    private int _nextVirtualRegister;
    private readonly Dictionary<int, IRType> _virtualRegisterTypes = [];
    private readonly Dictionary<int, int> _virtualRegisterClasses = [];

    public int CreateVirtualRegister(IRType type)
    {
        var virtualRegister = _nextVirtualRegister++;
        _virtualRegisterTypes[virtualRegister] = type;
        return virtualRegister;
    }

    public IRType GetVirtualRegisterType(int virtualRegister) =>
        _virtualRegisterTypes[virtualRegister];

    public bool TryGetVirtualRegisterType(int virtualRegister, out IRType type) =>
        _virtualRegisterTypes.TryGetValue(virtualRegister, out type!);

    // Called at the end of InstructionSelect once vregs have been constrained to
    // register classes and types are no longer needed for printing or downstream passes.
    public void ClearVirtualRegisterTypes() => _virtualRegisterTypes.Clear();

    // Set or refine a vreg's register class. Throws on a conflicting reassignment.
    // (We don't have subclass relationships yet, so reassignment requires either an
    // exact match or an unset entry — there's no class-intersection logic.)
    public void SetVirtualRegisterClass(int virtualRegister, int classId)
    {
        if (_virtualRegisterClasses.TryGetValue(virtualRegister, out var existing) && existing != classId)
            throw new InvalidOperationException(
                $"Virtual register %{virtualRegister} already constrained to class {existing}; cannot reassign to {classId}.");
        _virtualRegisterClasses[virtualRegister] = classId;
    }

    public bool TryGetVirtualRegisterClass(int virtualRegister, out int classId) =>
        _virtualRegisterClasses.TryGetValue(virtualRegister, out classId);

    public MachineBasicBlock CreateBasicBlock(Action<MachineBasicBlock>? configure = null)
    {
        var block = new MachineBasicBlock();
        block.Parent = this;
        configure?.Invoke(block);
        Blocks.Add(block);
        return block;
    }

    // Used by the parser to register a virtual register with a caller-supplied ID.
    internal void RegisterVirtualRegister(int id, IRType type)
    {
        _virtualRegisterTypes[id] = type;
        if (id >= _nextVirtualRegister)
            _nextVirtualRegister = id + 1;
    }

    // Used by the parser to register a class-tagged vreg (post-isel MIR) with no type info.
    internal void RegisterVirtualRegisterWithClass(int id, int classId)
    {
        _virtualRegisterClasses[id] = classId;
        if (id >= _nextVirtualRegister)
            _nextVirtualRegister = id + 1;
    }

    // Allocates a fresh vreg constrained to the given register class.
    // Used by passes that run post-isel where vregs carry classes rather than types.
    public int CreateVirtualRegisterWithClass(int classId)
    {
        var id = _nextVirtualRegister++;
        _virtualRegisterClasses[id] = classId;
        return id;
    }

    // Called after RegisterAllocatorPass once all vregs have been replaced with physregs.
    public void ClearVirtualRegisterClasses() => _virtualRegisterClasses.Clear();

    // Linear scan for the unique defining instruction of a virtual register.
    public MachineInstruction? GetDefinition(int virtualRegister)
    {
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualRegisterOperand v
                        && v.IsDefinition
                        && v.VirtualRegister == virtualRegister)
                        return instr;
        return null;
    }

    public int GetUseCount(int virtualRegister)
    {
        var count = 0;
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualRegisterOperand v
                        && !v.IsDefinition
                        && v.VirtualRegister == virtualRegister)
                        count++;
        return count;
    }

    // SSA RAUW: rewrite every non-def use of oldVreg to point at newVreg.
    public void ReplaceAllUsesOfRegister(int oldVreg, int newVreg)
    {
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is VirtualRegisterOperand v
                        && !v.IsDefinition
                        && v.VirtualRegister == oldVreg)
                    {
                        operands[i] = v with { VirtualRegister = newVreg };
                    }
                }
            }
        }
    }

    // An instruction is trivially dead if its opcode has no side effects, all of its
    // defs are virtual registers, and every virtual-register def has zero uses.
    public bool IsTriviallyDead(MachineInstruction instr)
    {
        if (!IsSideEffectFree(instr.Opcode))
            return false;

        var hasVRegDef = false;
        foreach (var operand in instr.Operands)
        {
            switch (operand)
            {
                case PhysicalRegisterOperand p when p.IsDefinition:
                    return false; // phys-reg def is observable
                case VirtualRegisterOperand v when v.IsDefinition:
                    hasVRegDef = true;
                    if (GetUseCount(v.VirtualRegister) > 0)
                        return false;
                    break;
            }
        }

        return hasVRegDef;
    }

    // Rebuild predecessor/successor lists from terminator block operands.
    // Run at the start of any pass that needs CFG edges (e.g. LivenessAnalysisPass).
    public void RebuildCfg()
    {
        foreach (var block in Blocks)
        {
            block.Predecessors.Clear();
            block.Successors.Clear();
        }

        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                {
                    if (op is not BlockOperand blockOp) continue;
                    var succ = blockOp.Block;
                    if (!block.Successors.Contains(succ))
                        block.Successors.Add(succ);
                    if (!succ.Predecessors.Contains(block))
                        succ.Predecessors.Add(block);
                }
            }
        }
    }

    private static bool IsSideEffectFree(int opcode) => opcode switch
    {
        GenericOpcode.GenericConstant
            or GenericOpcode.GenericCopy
            or GenericOpcode.GenericAdd
            or GenericOpcode.GenericSubtract
            or GenericOpcode.GenericAnd
            or GenericOpcode.GenericOr
            or GenericOpcode.GenericXor
            or GenericOpcode.GenericShiftLeft
            or GenericOpcode.GenericLogicalShiftRight
            or GenericOpcode.GenericArithmeticShiftRight
            or GenericOpcode.GenericZeroExtend
            or GenericOpcode.GenericSignExtend
            or GenericOpcode.GenericTruncate
            or GenericOpcode.GenericMerge
            or GenericOpcode.GenericUnmerge
            or GenericOpcode.GenericAddCarry => true,
        _ => false,
    };
}
