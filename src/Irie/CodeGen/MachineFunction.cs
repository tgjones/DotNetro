using Irie.IR;

namespace Irie.CodeGen;

public sealed class MachineFunction(string name)
{
    public string Name => name;

    public List<MachineBasicBlock> Blocks { get; } = [];

    private int _nextVirtualRegister;
    private readonly Dictionary<int, IRType> _virtualRegisterTypes = [];

    public int CreateVirtualRegister(IRType type)
    {
        var virtualRegister = _nextVirtualRegister++;
        _virtualRegisterTypes[virtualRegister] = type;
        return virtualRegister;
    }

    public IRType GetVirtualRegisterType(int virtualRegister) =>
        _virtualRegisterTypes[virtualRegister];

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
