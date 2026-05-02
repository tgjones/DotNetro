using Irie.CodeGen.Parsing;

namespace Irie.CodeGen;

public sealed class MachineModule
{
    public List<MachineFunction> Functions { get; } = [];

    public Func<int, string?>? OpcodeNamer { get; set; }
    public Func<int, string>? RegisterNamer { get; set; }
    public Func<int, string?>? RegisterClassNamer { get; set; }

    public MachineFunction CreateFunction(string name, Action<MachineFunction> configure)
    {
        var function = new MachineFunction(name);
        configure(function);
        Functions.Add(function);
        return function;
    }

    public static MachineModule Parse(TextReader reader) => MachineParser.Parse(reader);

    public void Write(TextWriter writer) => MachineWriter.Write(this, writer);
}
