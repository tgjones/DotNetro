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
}
