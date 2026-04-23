using static Irie.IR.Binary.IRBinaryFormat;

namespace Irie.IR.Binary;

internal static class IRBinaryWriter
{

    public static void Write(IRModule module, BinaryWriter writer)
    {
        writer.Write(module.Functions.Count);
        foreach (var function in module.Functions)
            Write(function, writer);
    }

    private static void Write(IRFunction function, BinaryWriter writer)
    {
        writer.Write(function.Name);
        writer.Write(function.ParameterTypes.Length);
        foreach (var type in function.ParameterTypes)
            WriteType(type, writer);
        WriteType(function.ReturnType, writer);

        var valueIndices = new Dictionary<IRValue, int>();
        var nextIndex = 0;

        writer.Write(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            writer.Write(block.Arguments.Count);
            foreach (var arg in block.Arguments)
            {
                WriteType(arg.Type, writer);
                valueIndices[arg] = nextIndex++;
            }

            writer.Write(block.Instructions.Count);
            foreach (var instruction in block.Instructions)
            {
                WriteInstruction(instruction, valueIndices, writer);
                if (instruction.HasResult)
                    valueIndices[instruction] = nextIndex++;
            }
        }
    }

    private static void WriteInstruction(IRInstruction instruction, Dictionary<IRValue, int> valueIndices, BinaryWriter writer)
    {
        switch (instruction)
        {
            case IRIntegerLiteralInstruction lit:
                writer.Write((byte)OpcodeTag.IntegerLiteral);
                WriteType(lit.Type, writer);
                writer.Write(lit.Value);
                break;

            case IRBinaryOperatorInstruction bin:
                writer.Write((byte)(bin.Kind switch
                {
                    BinaryOperatorKind.IntegerAdd => OpcodeTag.IntegerAdd,
                    _ => throw new InvalidOperationException($"Unknown binary operator: {bin.Kind}"),
                }));
                writer.Write(valueIndices[bin.Operands[0].Value]);
                writer.Write(valueIndices[bin.Operands[1].Value]);
                break;

            case IRReturnInstruction ret:
                writer.Write((byte)OpcodeTag.Return);
                writer.Write(ret.Operands.Length > 0);
                if (ret.Operands.Length > 0)
                    writer.Write(valueIndices[ret.Operands[0].Value]);
                break;

            default:
                throw new InvalidOperationException($"Unknown instruction type: {instruction.GetType().Name}");
        }
    }

    private static void WriteType(IRType type, BinaryWriter writer)
    {
        writer.Write((byte)(type switch
        {
            VoidType => TypeTag.Void,
            IntegerType { SizeInBits: 8 } => TypeTag.I8,
            IntegerType { SizeInBits: 16 } => TypeTag.I16,
            IntegerType { SizeInBits: 32 } => TypeTag.I32,
            _ => throw new InvalidOperationException($"Cannot serialize type: {type.DisplayName}"),
        }));
    }
}
