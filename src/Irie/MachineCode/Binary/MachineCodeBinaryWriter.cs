using static Irie.MachineCode.Binary.MachineCodeBinaryFormat;

namespace Irie.MachineCode.Binary;

internal static class MachineCodeBinaryWriter
{
    public static void Write(MachineCodeModule module, BinaryWriter writer)
    {
        // Globals are encoded only in the final binary; they're not part of the
        // MachineCode binary round-trip yet.
        writer.Write(module.Functions.Count);
        foreach (var function in module.Functions)
            Write(function, writer);
    }

    private static void Write(MachineCodeFunction function, BinaryWriter writer)
    {
        writer.Write(function.Name);
        writer.Write(function.Body.Count);
        foreach (var entry in function.Body)
            Write(entry, writer);
    }

    private static void Write(MachineCodeEntry entry, BinaryWriter writer)
    {
        switch (entry)
        {
            case MachineCodeLabel(var name):
                writer.Write((byte)EntryKindTag.Label);
                writer.Write(name);
                break;

            case MachineCodeInstruction(var opcode, var operands):
                writer.Write((byte)EntryKindTag.Instruction);
                writer.Write((byte)opcode);
                writer.Write((byte)operands.Length);
                foreach (var operand in operands)
                    Write(operand, writer);
                break;

            default:
                throw new InvalidOperationException($"Unknown entry type: {entry.GetType().Name}");
        }
    }

    private static void Write(MachineCodeOperand operand, BinaryWriter writer)
    {
        switch (operand)
        {
            case MachineCodeOperand.Register(var regNum):
                writer.Write((byte)OperandKindTag.Register);
                writer.Write(regNum);
                break;
            case MachineCodeOperand.Immediate(var value):
                writer.Write((byte)OperandKindTag.Immediate);
                writer.Write(value);
                break;
            case MachineCodeOperand.LabelRef(var name):
                writer.Write((byte)OperandKindTag.LabelRef);
                writer.Write(name);
                break;
            case MachineCodeOperand.ExternalRef(var name, var half):
                writer.Write((byte)OperandKindTag.ExternalRef);
                writer.Write(name);
                writer.Write((byte)half);
                break;
            default:
                throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}");
        }
    }
}
