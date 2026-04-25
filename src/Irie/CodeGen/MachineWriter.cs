namespace Irie.CodeGen;

internal static class MachineWriter
{
    public static void Write(MachineModule module, TextWriter writer)
    {
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0)
                writer.WriteLine();
            Write(module.Functions[i], writer);
        }
    }

    private static void Write(MachineFunction function, TextWriter writer)
    {
        var paramTypes = string.Join(", ", function.ParameterTypes.Select(t => t.DisplayName));
        writer.WriteLine($"func @{function.Name} : ({paramTypes}) -> {function.ReturnType.DisplayName} {{");

        var blockIndex = new Dictionary<MachineBasicBlock, int>();
        for (var i = 0; i < function.Blocks.Count; i++)
            blockIndex[function.Blocks[i]] = i;

        for (var i = 0; i < function.Blocks.Count; i++)
            Write(function.Blocks[i], i, function, blockIndex, writer);

        writer.WriteLine("}");
    }

    private static void Write(
        MachineBasicBlock block,
        int index,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex,
        TextWriter writer)
    {
        var paramStr = string.Join(", ", block.Parameters.Select(vreg =>
            $"%{vreg}:{function.GetVirtualRegisterType(vreg).DisplayName}"));
        writer.WriteLine($"  bb{index}({paramStr}):");

        foreach (var instruction in block.Instructions)
            Write(instruction, function, blockIndex, writer);
    }

    private static void Write(
        MachineInstruction instruction,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex,
        TextWriter writer)
    {
        writer.Write("    ");

        var defs = instruction.Operands.Where(IsDefinition).ToList();
        if (defs.Count > 0)
        {
            writer.Write(string.Join(", ", defs.Select(d => FormatDefinition(d, function))));
            writer.Write(" = ");
        }

        writer.Write(GetOpcodeName(instruction.Opcode));

        var uses = instruction.Operands.Where(o => !IsDefinition(o)).ToList();
        if (uses.Count > 0)
        {
            writer.Write(" ");
            writer.Write(string.Join(", ", uses.Select(u => FormatOperand(u, function, blockIndex))));
        }

        writer.WriteLine();
    }

    private static bool IsDefinition(MachineOperand operand) => operand switch
    {
        VirtualRegisterOperand vreg => vreg.IsDefinition,
        PhysicalRegisterOperand phys => phys.IsDefinition,
        _ => false,
    };

    private static string FormatDefinition(MachineOperand operand, MachineFunction function) => operand switch
    {
        VirtualRegisterOperand vreg => $"%{vreg.VirtualRegister}:{function.GetVirtualRegisterType(vreg.VirtualRegister).DisplayName}",
        PhysicalRegisterOperand phys => $"${phys.Register}",
        _ => throw new InvalidOperationException($"Not a definition operand: {operand.GetType().Name}"),
    };

    private static string FormatOperand(
        MachineOperand operand,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex) => operand switch
    {
        VirtualRegisterOperand vreg => $"%{vreg.VirtualRegister}",
        PhysicalRegisterOperand phys => $"${phys.Register}",
        ImmediateOperand imm => imm.Value.ToString(),
        BlockOperand block => $"bb{blockIndex[block.Block]}",
        ExternalSymbolOperand sym => $"@{sym.Name}",
        _ => throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}"),
    };

    private static string GetOpcodeName(int opcode) =>
        GenericOpcode.GetName(opcode) ?? $"opcode({opcode})";
}
