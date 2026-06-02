using Irie.Mir.Binary;
using Irie.Mir.Parsing;
using Irie.Mir.Writing;

namespace Irie.Mir;

public sealed class MirModule
{
    public List<MirFunction> Functions { get; } = [];

    // Module-level named regions of memory: strings, static fields, vtables,
    // and frame slot stash space. See MirGlobal for the data model.
    public List<MirGlobal> Globals { get; } = [];

    public MirFunction CreateFunction(string name, IRType[] paramTypes, IRType returnType, Action<MirFunction> configure)
    {
        var function = new MirFunction(name, paramTypes, returnType);
        configure(function);
        Functions.Add(function);
        return function;
    }

    public static MirModule Parse(TextReader reader) => MirParser.Parse(reader);

    public static MirModule Read(BinaryReader reader) => MirBinaryReader.Read(reader);

    public void Write(TextWriter writer, Func<int, string>? physRegNamer = null) =>
        MirWriter.Write(this, writer, physRegNamer);

    public void Write(BinaryWriter writer) => MirBinaryWriter.Write(this, writer);
}
