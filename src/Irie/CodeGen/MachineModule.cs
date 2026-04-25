using Irie.CodeGen.Parsing;
using Irie.IR;

namespace Irie.CodeGen;

public sealed class MachineModule
{
    public List<MachineFunction> Functions { get; } = [];

    public MachineFunction CreateFunction(string name, IRType[] parameterTypes, IRType returnType, Action<MachineFunction> configure)
    {
        var function = new MachineFunction(name, parameterTypes, returnType);
        configure(function);
        Functions.Add(function);
        return function;
    }

    public static MachineModule Parse(TextReader reader) => MachineParser.Parse(reader);

    public void Write(TextWriter writer) => MachineWriter.Write(this, writer);
}
