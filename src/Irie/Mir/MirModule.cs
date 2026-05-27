using Irie.IR;
using Irie.Mir.Parsing;
using Irie.Mir.Writing;

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

    public static MirModule Parse(TextReader reader) => MirParser.Parse(reader);

    public void Write(TextWriter writer) => MirWriter.Write(this, writer);
}
