
namespace Irie.IR;

internal static class IRWriter
{
    public static void Write(IRModule module, TextWriter writer)
    {
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0)
            {
                writer.WriteLine();
            }

            Write(module.Functions[i], writer);
        }
    }
    
    private static void Write(IRFunction function, TextWriter writer)
    {
        writer.WriteLine($"func @{function.Name} : ({GetParameters(function)}) -> {function.ReturnType.DisplayName} {{");

        var blockIndex = 0;

        Dictionary<IRValue, int> valueIndices = new();

        string GetValueDisplay(IRValue value)
        {
            if (!valueIndices.TryGetValue(value, out var result))
            {
                valueIndices.Add(value, result = valueIndices.Count);
            }
            return $"%{result}";
        }

        string GetBlockArguments(IRBasicBlock block)
        {
            return string.Join(", ", block.Arguments.Select(a => $"{GetValueDisplay(a)} : {a.Type.DisplayName}"));
        }

        foreach (var block in function.Blocks)
        {
            writer.WriteLine($"bb{blockIndex++}({GetBlockArguments(block)}):");
            foreach (var instruction in block.Instructions)
            {
                writer.Write("  ");
                if (instruction.HasResult)
                {
                    writer.Write($"{GetValueDisplay(instruction)} = ");
                }
                writer.Write(instruction switch
                {
                    IRBinaryOperatorInstruction b => b.Kind switch
                    {
                        BinaryOperatorKind.IntegerAdd => "integer_add",
                        _ => throw new InvalidOperationException(),
                    },
                    IRIntegerLiteralInstruction i => $"integer_literal {i.Type.DisplayName} {i.Value}",
                    IRReturnInstruction => "return",
                    _ => throw new InvalidOperationException(),
                });
                if (instruction.Operands.Length > 0)
                {
                    writer.Write(" ");
                }
                var first = true;
                foreach (var operand in instruction.Operands)
                {
                    if (!first)
                    {
                        writer.Write(", ");
                    }
                    writer.Write(GetValueDisplay(operand.Value));
                    first = false;
                }
                writer.WriteLine();
            }
        }

        writer.WriteLine("}");
    }

    private static string GetParameters(IRFunction function)
    {
        return string.Join(", ", function.ParameterTypes.Select(t => t.DisplayName));
    }
}
