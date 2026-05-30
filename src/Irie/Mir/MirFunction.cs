
namespace Irie.Mir;

public sealed class MirFunction(string name, IRType[] paramTypes, IRType returnType)
{
    public string Name { get; } = name;

    // Signature is preserved by every pass — even after ABI lowering replaces
    // entry-block parameters with pseudo.copy from physregs.
    public IRType[] ParameterTypes { get; } = paramTypes;
    public IRType   ReturnType    { get; } = returnType;

    public List<MirBlock> Blocks { get; } = [];

    private int _nextVirtualRegister;
    private readonly Dictionary<int, VRegAnnotation> _annotations = [];

    public int CreateVirtualRegister(IRType type)
    {
        var vreg = _nextVirtualRegister++;
        _annotations[vreg] = new TypedVReg(type);
        return vreg;
    }

    public int CreateVirtualRegisterInClass(int classId, string className)
    {
        var vreg = _nextVirtualRegister++;
        _annotations[vreg] = new ClassedVReg(classId, className);
        return vreg;
    }

    public VRegAnnotation GetVRegAnnotation(int vreg) => _annotations[vreg];

    public bool TryGetVRegAnnotation(int vreg, out VRegAnnotation annotation) =>
        _annotations.TryGetValue(vreg, out annotation!);

    // Live virtual-register IDs in dictionary insertion order — stable across
    // round-trips because both the text parser and the binary reader register
    // vregs in the order they appear.
    public IReadOnlyCollection<int> VirtualRegisterIds => _annotations.Keys;

    // Replace a vreg's TypedVReg entry with a ClassedVReg. Called by
    // instruction selection when it commits a vreg to a register class.
    public void ReclassifyVirtualRegister(int vreg, int classId, string className)
    {
        _annotations[vreg] = new ClassedVReg(classId, className);
    }

    // Used by parsers to register a vreg with a caller-supplied ID.
    internal void RegisterVirtualRegister(int id, VRegAnnotation annotation)
    {
        _annotations[id] = annotation;
        if (id >= _nextVirtualRegister)
            _nextVirtualRegister = id + 1;
    }

    // Drop the vreg table after RA has replaced every VirtualReg with PhysicalReg.
    public void ClearVRegAnnotations() => _annotations.Clear();

    public MirBlock CreateBlock(Action<MirBlock>? configure = null)
    {
        var block = new MirBlock();
        block.Parent = this;
        configure?.Invoke(block);
        Blocks.Add(block);
        return block;
    }

    // Linear scan for the unique defining instruction of a virtual register.
    public MirInstruction? GetDefinition(int vreg)
    {
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualReg v && v.IsDefinition && v.Id == vreg)
                        return instr;
        return null;
    }

    public int GetUseCount(int vreg)
    {
        var count = 0;
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    count += CountUsesIn(operand, vreg);
        return count;
    }

    private static int CountUsesIn(MirOperand operand, int vreg) => operand switch
    {
        VirtualReg v when !v.IsDefinition && v.Id == vreg => 1,
        BlockTarget bt => CountUsesInArgs(bt.Args, vreg),
        _ => 0,
    };

    private static int CountUsesInArgs(MirOperand[] args, int vreg)
    {
        var count = 0;
        foreach (var arg in args)
            count += CountUsesIn(arg, vreg);
        return count;
    }

    // SSA RAUW: rewrite every non-def use of oldVreg to point at newVreg,
    // including uses nested inside BlockTarget.Args.
    public void ReplaceAllUsesOfRegister(int oldVreg, int newVreg)
    {
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                    operands[i] = ReplaceUseIn(operands[i], oldVreg, newVreg);
            }
        }
    }

    private static MirOperand ReplaceUseIn(MirOperand operand, int oldVreg, int newVreg) => operand switch
    {
        VirtualReg v when !v.IsDefinition && v.Id == oldVreg => v with { Id = newVreg },
        BlockTarget bt => bt with { Args = ReplaceUsesInArgs(bt.Args, oldVreg, newVreg) },
        _ => operand,
    };

    private static MirOperand[] ReplaceUsesInArgs(MirOperand[] args, int oldVreg, int newVreg)
    {
        var rewritten = new MirOperand[args.Length];
        for (var i = 0; i < args.Length; i++)
            rewritten[i] = ReplaceUseIn(args[i], oldVreg, newVreg);
        return rewritten;
    }

    // Trivially dead: side-effect-free opcode, no physical-register defs, and
    // every virtual-register def has zero uses.
    public bool IsTriviallyDead(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        if (!dialect.IsSideEffectFree(instr.Opcode.Code))
            return false;

        var hasVRegDef = false;
        foreach (var operand in instr.Operands)
        {
            switch (operand)
            {
                case PhysicalReg p when p.IsDefinition:
                    return false; // phys-reg def is observable
                case VirtualReg v when v.IsDefinition:
                    hasVRegDef = true;
                    if (GetUseCount(v.Id) > 0)
                        return false;
                    break;
            }
        }

        return hasVRegDef;
    }

    // Rebuild predecessor/successor lists from terminator BlockTarget operands.
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
                    if (op is not BlockTarget target) continue;
                    var succ = target.Block;
                    if (!block.Successors.Contains(succ))
                        block.Successors.Add(succ);
                    if (!succ.Predecessors.Contains(block))
                        succ.Predecessors.Add(block);
                }
            }
        }
    }
}
