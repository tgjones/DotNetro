using static Irie.MachineCode.Binary.MachineCodeBinaryFormat;

namespace Irie.MachineCode.Binary;

internal static class MachineCodeBinaryReader
{
    public static MachineCodeModule Read(BinaryReader reader)
    {
        var module = new MachineCodeModule();
        var functionCount = reader.ReadInt32();
        for (var i = 0; i < functionCount; i++)
            module.Functions.Add(ReadFunction(reader));
        return module;
    }

    private static MachineCodeFunction ReadFunction(BinaryReader reader)
    {
        var name = reader.ReadString();
        var function = new MachineCodeFunction(name);
        var entryCount = reader.ReadInt32();
        for (var i = 0; i < entryCount; i++)
            function.Body.Add(ReadEntry(reader));
        return function;
    }

    private static MachineCodeEntry ReadEntry(BinaryReader reader)
    {
        var tag = (EntryKindTag)reader.ReadByte();
        return tag switch
        {
            EntryKindTag.Label => new MachineCodeLabel(reader.ReadString()),
            EntryKindTag.Instruction => ReadInstruction(reader),
            _ => throw new InvalidDataException($"Unknown entry kind tag: {tag}"),
        };
    }

    private static MachineCodeInstruction ReadInstruction(BinaryReader reader)
    {
        var opcode = reader.ReadByte();
        var operandCount = reader.ReadByte();
        var operands = new MachineCodeOperand[operandCount];
        for (var i = 0; i < operandCount; i++)
            operands[i] = ReadOperand(reader);
        return new MachineCodeInstruction(opcode, operands);
    }

    private static MachineCodeOperand ReadOperand(BinaryReader reader)
    {
        var tag = (OperandKindTag)reader.ReadByte();
        return tag switch
        {
            OperandKindTag.Register    => new MachineCodeOperand.Register(reader.ReadInt32()),
            OperandKindTag.Immediate   => new MachineCodeOperand.Immediate(reader.ReadInt64()),
            OperandKindTag.LabelRef    => new MachineCodeOperand.LabelRef(reader.ReadString()),
            OperandKindTag.ExternalRef => new MachineCodeOperand.ExternalRef(reader.ReadString(), (SymbolHalf)reader.ReadByte(), reader.ReadInt32()),
            _ => throw new InvalidDataException($"Unknown operand kind tag: {tag}"),
        };
    }
}
