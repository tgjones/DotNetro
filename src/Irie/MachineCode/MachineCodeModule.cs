using Irie.MachineCode.Binary;

namespace Irie.MachineCode;

public sealed class MachineCodeModule
{
    public List<MachineCodeFunction> Functions { get; } = [];

    public MachineCodeFunction CreateFunction(string name)
    {
        var function = new MachineCodeFunction(name);
        Functions.Add(function);
        return function;
    }

    public static MachineCodeModule Read(BinaryReader reader) => MachineCodeBinaryReader.Read(reader);

    public void Write(BinaryWriter writer) => MachineCodeBinaryWriter.Write(this, writer);
}
