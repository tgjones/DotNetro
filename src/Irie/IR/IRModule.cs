namespace Irie.IR;

public sealed class IRModule
{
    public List<IRFunction> Functions { get; } = [];

    public IRFunction CreateFunction(string name, IRType[] parameterTypes, IRType returnType, Action<IRFunction> configure)
    {
        var function = new IRFunction(name, parameterTypes, returnType);
        configure(function);
        Functions.Add(function);
        return function;
    }

    public void Write(TextWriter writer)
    {
        IRWriter.Write(this, writer);
    }
}
