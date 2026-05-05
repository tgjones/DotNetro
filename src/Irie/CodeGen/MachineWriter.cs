namespace Irie.CodeGen;

internal sealed class MachineWriter(Target target)
{
    private readonly TargetMIRInfo _mirInfo = target.CreateMIRInfo();
    private readonly TargetInstructionInfo _instrInfo = target.CreateInstructionInfo();

    public static void Write(MachineModule module, TextWriter writer) =>
        new MachineWriter(module.Target).WriteAll(module, writer);

    private void WriteAll(MachineModule module, TextWriter writer)
    {
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0)
                writer.WriteLine();
            WriteFunction(module.Functions[i], writer);
        }
    }

    private void WriteFunction(MachineFunction function, TextWriter writer)
    {
        writer.WriteLine($"func @{function.Name} {{");

        var blockIndex = new Dictionary<MachineBasicBlock, int>();
        for (var i = 0; i < function.Blocks.Count; i++)
            blockIndex[function.Blocks[i]] = i;

        for (var i = 0; i < function.Blocks.Count; i++)
            WriteBlock(function.Blocks[i], i, function, blockIndex, writer);

        writer.WriteLine("}");
    }

    private void WriteBlock(
        MachineBasicBlock block,
        int index,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex,
        TextWriter writer)
    {
        var paramStr = string.Join(", ", block.Parameters.Select(vreg =>
            $"%{vreg}:{FormatVirtualRegisterAnnotation(vreg, function)}"));
        writer.WriteLine($"  bb{index}({paramStr}):");

        if (block.LiveIns.Count > 0)
            writer.WriteLine($"    liveins: {string.Join(", ", block.LiveIns.Select(r => $"${FormatPhysReg(r)}"))}");

        foreach (var instruction in block.Instructions)
            WriteInstruction(instruction, function, blockIndex, writer);
    }

    private void WriteInstruction(
        MachineInstruction instruction,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex,
        TextWriter writer)
    {
        writer.Write("    ");

        var tiedOperands = _instrInfo.TryGet(instruction.Opcode)?.TiedOperands;
        var defStrings = new List<string>();
        var useStrings = new List<string>();
        var implicitStrings = new List<string>();
        for (var i = 0; i < instruction.Operands.Length; i++)
        {
            var op = instruction.Operands[i];
            if (op is PhysicalRegisterOperand { IsImplicit: true } phys)
            {
                implicitStrings.Add($"{(phys.IsDefinition ? "implicit-def" : "implicit")} ${FormatPhysReg(phys.Register)}");
            }
            else if (IsDefinition(op))
            {
                defStrings.Add(FormatDefinition(op, function));
            }
            else
            {
                var tiedTo = tiedOperands != null && i < tiedOperands.Length ? tiedOperands[i] : -1;
                useStrings.Add(FormatUseOperand(op, function, blockIndex, tiedTo));
            }
        }

        if (defStrings.Count > 0)
        {
            writer.Write(string.Join(", ", defStrings));
            writer.Write(" = ");
        }

        writer.Write(GetOpcodeName(instruction.Opcode));

        var allUseStrings = useStrings.Concat(implicitStrings).ToList();
        if (allUseStrings.Count > 0)
        {
            writer.Write(" ");
            writer.Write(string.Join(", ", allUseStrings));
        }

        writer.WriteLine();
    }

    private static bool IsDefinition(MachineOperand operand) => operand switch
    {
        VirtualRegisterOperand vreg => vreg.IsDefinition,
        PhysicalRegisterOperand phys => phys.IsDefinition,
        _ => false,
    };

    private string FormatDefinition(MachineOperand operand, MachineFunction function) => operand switch
    {
        VirtualRegisterOperand vreg => $"%{vreg.VirtualRegister}:{FormatVirtualRegisterAnnotation(vreg.VirtualRegister, function)}",
        PhysicalRegisterOperand phys => $"${FormatPhysReg(phys.Register)}",
        _ => throw new InvalidOperationException($"Not a definition operand: {operand.GetType().Name}"),
    };

    // Print the register class if assigned (post-isel), otherwise the generic type.
    // After InstructionSelectorPass calls ClearVirtualRegisterTypes, vregs that
    // received a class show only that class; vregs without a class (e.g. unselected
    // GenericCopy defs) still show their type.
    private string FormatVirtualRegisterAnnotation(int virtualRegister, MachineFunction function)
    {
        if (function.TryGetVirtualRegisterClass(virtualRegister, out var classId))
            return _mirInfo.GetRegisterClassName(classId) ?? $"class({classId})";
        if (function.TryGetVirtualRegisterType(virtualRegister, out var type))
            return type.DisplayName;
        return "?";
    }

    // Format a use operand. For vregs that have a register class assigned, prints
    // "%N:ClassName", and additionally "(tied-def M)" if tied to a def operand.
    private string FormatUseOperand(
        MachineOperand operand,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex,
        int tiedToDefIndex)
    {
        if (operand is VirtualRegisterOperand vreg
            && function.TryGetVirtualRegisterClass(vreg.VirtualRegister, out var classId))
        {
            var className = _mirInfo.GetRegisterClassName(classId) ?? $"class({classId})";
            return tiedToDefIndex >= 0
                ? $"%{vreg.VirtualRegister}:{className}(tied-def {tiedToDefIndex})"
                : $"%{vreg.VirtualRegister}:{className}";
        }
        return FormatOperand(operand, function, blockIndex);
    }

    private string FormatOperand(
        MachineOperand operand,
        MachineFunction function,
        Dictionary<MachineBasicBlock, int> blockIndex) => operand switch
    {
        VirtualRegisterOperand vreg => $"%{vreg.VirtualRegister}",
        PhysicalRegisterOperand phys => $"${FormatPhysReg(phys.Register)}",
        ImmediateOperand imm => imm.Value.ToString(),
        BlockOperand block => $"bb{blockIndex[block.Block]}",
        ExternalSymbolOperand sym => $"@{sym.Name}",
        _ => throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}"),
    };

    private string FormatPhysReg(int register) => _mirInfo.GetRegisterName(register);

    private string GetOpcodeName(int opcode) =>
        GenericOpcode.GetName(opcode)
        ?? _instrInfo.GetDisplayName(opcode)
        ?? $"opcode({opcode})";
}
