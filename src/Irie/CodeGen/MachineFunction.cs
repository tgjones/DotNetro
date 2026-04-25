using Irie.IR;

namespace Irie.CodeGen;

public sealed class MachineFunction(string name, IRType[] parameterTypes, IRType returnType)
{
    public string Name => name;

    public IRType[] ParameterTypes => parameterTypes;

    public IRType ReturnType => returnType;

    public List<MachineBasicBlock> Blocks { get; } = [];

    private int _nextVirtualRegister;
    private readonly Dictionary<int, IRType> _virtualRegisterTypes = [];

    public int CreateVirtualRegister(IRType type)
    {
        var virtualRegister = _nextVirtualRegister++;
        _virtualRegisterTypes[virtualRegister] = type;
        return virtualRegister;
    }

    // Creates a virtual register and immediately registers it as a parameter of the given block.
    public int CreateBlockParameter(MachineBasicBlock block, IRType type)
    {
        var virtualRegister = CreateVirtualRegister(type);
        block.Parameters.Add(virtualRegister);
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
}
