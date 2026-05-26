using Irie.IR;

namespace Irie.Mir;

public sealed class MirModule
{
    public List<MirFunction> Functions { get; } = [];

    public MirFunction CreateFunction(string name, IRType[] paramTypes, IRType returnType, Action<MirFunction> configure)
    {
        var function = new MirFunction(name, paramTypes, returnType);
        configure(function);
        Functions.Add(function);
        return function;
    }
}
