using Irie.IR.Binary;
using Irie.IR.Parsing;

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

    public static IRModule Parse(TextReader reader) => IRParser.Parse(reader);

    public static IRModule Read(BinaryReader reader) => IRBinaryReader.Read(reader);

    public void Write(TextWriter writer) => IRWriter.Write(this, writer);

    public void Write(BinaryWriter writer) => IRBinaryWriter.Write(this, writer);
}
