using Irie.CodeGen.Parsing;

namespace Irie.CodeGen;

public sealed class MachineModule(Target target)
{
    public Target Target { get; } = target;

    public List<MachineFunction> Functions { get; } = [];

    public MachineFunction CreateFunction(string name, Action<MachineFunction> configure)
    {
        var function = new MachineFunction(name);
        configure(function);
        Functions.Add(function);
        return function;
    }

    public static MachineModule Parse(TextReader reader, Target target) =>
        MachineParser.Parse(reader, target);

    public void Write(TextWriter writer) => MachineWriter.Write(this, writer);
}
