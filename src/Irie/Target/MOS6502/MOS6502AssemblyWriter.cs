using Irie.MachineCode;

namespace Irie.Target.MOS6502;

public static class MOS6502AssemblyWriter
{
    public static void Write(MachineCodeModule module, TextWriter writer)
    {
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0)
                writer.WriteLine();
            Write(module.Functions[i], writer);
        }
    }

    private static void Write(MachineCodeFunction function, TextWriter writer)
    {
        writer.WriteLine($"{function.Name}:");
        foreach (var entry in function.Body)
        {
            switch (entry)
            {
                case MachineCodeLabel(var name):
                    writer.WriteLine($".{name}:");
                    break;
                case MachineCodeInstruction instruction:
                    writer.Write("    ");
                    WriteInstruction(instruction, writer);
                    writer.WriteLine();
                    break;
            }
        }
    }

    private static void WriteInstruction(MachineCodeInstruction instruction, TextWriter writer)
    {
        var info = MOS6502InstructionInfo.Instance.Get(instruction.Opcode);
        writer.Write(info.Mnemonic);

        var operandText = FormatOperands(instruction, info);
        if (operandText != null)
        {
            writer.Write(' ');
            writer.Write(operandText);
        }
    }

    private static string? FormatOperands(MachineCodeInstruction instruction, MOS6502InstructionDescription info)
    {
        if (info.Mode == AddressingMode.Implied || instruction.Operands.Length == 0)
            return null;

        var operand = instruction.Operands[0];

        return info.Mode switch
        {
            AddressingMode.Immediate  => $"#{FormatImmediate(operand)}",
            AddressingMode.ZeroPage   => FormatAddress(operand, 1),
            AddressingMode.ZeroPageX  => $"{FormatAddress(operand, 1)},X",
            AddressingMode.ZeroPageY  => $"{FormatAddress(operand, 1)},Y",
            AddressingMode.Absolute   => FormatAddress(operand, 2),
            AddressingMode.AbsoluteX  => $"{FormatAddress(operand, 2)},X",
            AddressingMode.AbsoluteY  => $"{FormatAddress(operand, 2)},Y",
            AddressingMode.Indirect   => $"({FormatAddress(operand, 2)})",
            AddressingMode.IndirectX  => $"({FormatAddress(operand, 1)},X)",
            AddressingMode.IndirectY  => $"({FormatAddress(operand, 1)}),Y",
            AddressingMode.Relative   => FormatBranchTarget(operand),
            _ => throw new InvalidOperationException($"Unhandled addressing mode: {info.Mode}"),
        };
    }

    private static string FormatImmediate(MachineCodeOperand operand)
    {
        return operand switch
        {
            MachineCodeOperand.Immediate(var value) => $"${value & 0xFF:X2}",
            MachineCodeOperand.ExternalRef(var name, SymbolHalf.LowByte)  => $"<{name}",
            MachineCodeOperand.ExternalRef(var name, SymbolHalf.HighByte) => $">{name}",
            _ => throw new InvalidOperationException(
                $"Expected Immediate or symbol-half ExternalRef operand, got {operand.GetType().Name}"),
        };
    }

    private static string FormatAddress(MachineCodeOperand operand, int bytes)
    {
        return operand switch
        {
            MachineCodeOperand.Immediate(var value) =>
                bytes == 1 ? $"${value & 0xFF:X2}" : $"${value & 0xFFFF:X4}",
            MachineCodeOperand.ExternalRef(var name, _) => name,
            MachineCodeOperand.LabelRef(var name) => $".{name}",
            _ => throw new InvalidOperationException($"Unexpected operand type {operand.GetType().Name} for address"),
        };
    }

    private static string FormatBranchTarget(MachineCodeOperand operand)
    {
        return operand switch
        {
            MachineCodeOperand.LabelRef(var name)        => $".{name}",
            MachineCodeOperand.ExternalRef(var name, _)  => name,
            _ => throw new InvalidOperationException($"Unexpected operand type {operand.GetType().Name} for branch"),
        };
    }
}
