using Irie.MachineCode.Binary;

namespace Irie.MachineCode;

public sealed class MachineCodeModule
{
    public List<MachineCodeFunction> Functions { get; } = [];

    // Module-level named data regions: strings, static fields, vtables.
    // Populated by the MachineCodeEmitter from MirModule.Globals; consumed by
    // the binary encoder. Globals are NOT part of the MachineCode binary
    // round-trip format yet — see MachineCodeBinaryWriter.
    public List<MachineCodeGlobal> Globals { get; } = [];

    public MachineCodeFunction CreateFunction(string name)
    {
        var function = new MachineCodeFunction(name);
        Functions.Add(function);
        return function;
    }

    public static MachineCodeModule Read(BinaryReader reader) => MachineCodeBinaryReader.Read(reader);

    public void Write(BinaryWriter writer) => MachineCodeBinaryWriter.Write(this, writer);
}
