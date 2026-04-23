using static Irie.IR.Binary.IRBinaryFormat;

namespace Irie.IR.Binary;

internal static class IRBinaryReader
{
    public static IRModule Read(BinaryReader reader)
    {
        var module = new IRModule();
        var functionCount = reader.ReadInt32();
        for (var i = 0; i < functionCount; i++)
            module.Functions.Add(ReadFunction(reader));
        return module;
    }

    private static IRFunction ReadFunction(BinaryReader reader)
    {
        var name = reader.ReadString();
        var paramCount = reader.ReadInt32();
        var paramTypes = new IRType[paramCount];
        for (var i = 0; i < paramCount; i++)
            paramTypes[i] = ReadType(reader);
        var returnType = ReadType(reader);

        var function = new IRFunction(name, paramTypes, returnType);
        var valueMap = new Dictionary<int, IRValue>();
        var nextIndex = 0;

        var blockCount = reader.ReadInt32();
        for (var b = 0; b < blockCount; b++)
        {
            var block = new IRBasicBlock();

            var argCount = reader.ReadInt32();
            for (var a = 0; a < argCount; a++)
            {
                var type = ReadType(reader);
                valueMap[nextIndex++] = block.CreateArgument(type);
            }

            var instrCount = reader.ReadInt32();
            for (var i = 0; i < instrCount; i++)
            {
                var instruction = ReadInstruction(block, reader, valueMap);
                if (instruction.HasResult)
                    valueMap[nextIndex++] = instruction;
            }

            function.Blocks.Add(block);
        }

        return function;
    }

    private static IRInstruction ReadInstruction(IRBasicBlock block, BinaryReader reader, Dictionary<int, IRValue> valueMap)
    {
        var opcode = (OpcodeTag)reader.ReadByte();
        return opcode switch
        {
            OpcodeTag.IntegerLiteral => ReadIntegerLiteral(block, reader),
            OpcodeTag.IntegerAdd => ReadIntegerAdd(block, reader, valueMap),
            OpcodeTag.Return => ReadReturn(block, reader, valueMap),
            _ => throw new InvalidDataException($"Unknown opcode: {opcode}"),
        };
    }

    private static IRInstruction ReadIntegerLiteral(IRBasicBlock block, BinaryReader reader)
    {
        var type = ReadType(reader);
        var value = reader.ReadInt64();
        return block.CreateIntegerLiteral(type, value);
    }

    private static IRInstruction ReadIntegerAdd(IRBasicBlock block, BinaryReader reader, Dictionary<int, IRValue> valueMap)
    {
        var lhs = valueMap[reader.ReadInt32()];
        var rhs = valueMap[reader.ReadInt32()];
        return block.CreateIntegerAdd(lhs, rhs);
    }

    private static IRInstruction ReadReturn(IRBasicBlock block, BinaryReader reader, Dictionary<int, IRValue> valueMap)
    {
        return reader.ReadBoolean()
            ? block.CreateReturn(valueMap[reader.ReadInt32()])
            : block.CreateReturn();
    }

    private static IRType ReadType(BinaryReader reader)
    {
        var tag = (TypeTag)reader.ReadByte();
        return tag switch
        {
            TypeTag.Void => IRType.Void,
            TypeTag.I8 => IRType.I8,
            TypeTag.I16 => IRType.I16,
            TypeTag.I32 => IRType.I32,
            _ => throw new InvalidDataException($"Unknown type tag: {tag}"),
        };
    }
}
