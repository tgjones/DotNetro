
namespace Irie.Mir.Writing;

internal sealed class MirWriter
{
    // Optional lookup from physical-register ID to a printable name. When null
    // (the default), physregs print as raw numeric IDs (`$8`). When supplied
    // (e.g. by iriec wiring in the target's RegisterInfo), they print under
    // their target-specific names (`$RC2`).
    private readonly Func<int, string>? _physRegNamer;

    private MirWriter(Func<int, string>? physRegNamer) => _physRegNamer = physRegNamer;

    public static void Write(MirModule module, TextWriter writer, Func<int, string>? physRegNamer = null) =>
        new MirWriter(physRegNamer).WriteAll(module, writer);

    private void WriteAll(MirModule module, TextWriter writer)
    {
        MirBootstrap.EnsureRegistered();
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0) writer.WriteLine();
            WriteFunction(module.Functions[i], writer);
        }
    }

    private void WriteFunction(MirFunction function, TextWriter writer)
    {
        var paramStr = string.Join(", ", function.ParameterTypes.Select(FormatType));
        writer.WriteLine($"func @{function.Name} : ({paramStr}) -> {FormatType(function.ReturnType)} {{");

        var blockIndex = new Dictionary<MirBlock, int>();
        for (var i = 0; i < function.Blocks.Count; i++)
            blockIndex[function.Blocks[i]] = i;

        for (var i = 0; i < function.Blocks.Count; i++)
            WriteBlock(function.Blocks[i], i, function, blockIndex, writer);

        writer.WriteLine("}");
    }

    private void WriteBlock(
        MirBlock block,
        int index,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex,
        TextWriter writer)
    {
        var paramStr = string.Join(", ", block.Parameters.Select(vreg =>
            $"%{vreg} : {FormatAnnotation(function.GetVRegAnnotation(vreg))}"));
        writer.WriteLine($"bb{index}({paramStr}):");

        if (block.LiveIns.Count > 0)
            writer.WriteLine($"    [liveins: {string.Join(", ", block.LiveIns.Select(r => "$" + FormatPhysReg(r)))}]");

        foreach (var instruction in block.Instructions)
            WriteInstruction(instruction, function, blockIndex, writer);
    }

    private void WriteInstruction(
        MirInstruction instruction,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex,
        TextWriter writer)
    {
        writer.Write("    ");

        var dialect = DialectRegistry.ById(instruction.Opcode.Dialect);
        var info = dialect.GetInstructionInfo(instruction.Opcode.Code);
        var tied = info.TiedOperands;

        var defStrings = new List<string>();
        var useStrings = new List<string>();
        var implicitStrings = new List<string>();
        for (var i = 0; i < instruction.Operands.Length; i++)
        {
            var op = instruction.Operands[i];
            if (op is PhysicalReg { IsImplicit: true } phys)
            {
                implicitStrings.Add($"{(phys.IsDefinition ? "implicit-def" : "implicit")} ${FormatPhysReg(phys.Id)}");
            }
            else if (IsDefinition(op))
            {
                defStrings.Add(FormatDefinition(op, function));
            }
            else
            {
                var tiedTo = tied != null && i < tied.Length ? tied[i] : -1;
                useStrings.Add(FormatUseOperand(op, function, blockIndex, tiedTo));
            }
        }

        if (defStrings.Count > 0)
        {
            writer.Write(string.Join(", ", defStrings));
            writer.Write(" = ");
        }

        writer.Write($"{dialect.Prefix}.{dialect.GetOpName(instruction.Opcode.Code)}");

        var allUses = useStrings.Concat(implicitStrings).ToList();
        if (allUses.Count > 0)
        {
            writer.Write(" ");
            writer.Write(string.Join(", ", allUses));
        }

        writer.WriteLine();
    }

    private static bool IsDefinition(MirOperand operand) => operand switch
    {
        VirtualReg v  => v.IsDefinition,
        PhysicalReg p => p.IsDefinition,
        _ => false,
    };

    private string FormatDefinition(MirOperand operand, MirFunction function) => operand switch
    {
        VirtualReg v  => $"%{v.Id} : {FormatAnnotation(function.GetVRegAnnotation(v.Id))}",
        PhysicalReg p => $"${FormatPhysReg(p.Id)}",
        _ => throw new InvalidOperationException($"Not a definition operand: {operand.GetType().Name}"),
    };

    // Uses print unannotated per the format spec — the annotation is taken from
    // the def site. The one exception is `(tied-def N)`, pulled from the
    // dialect's TiedOperands metadata.
    private string FormatUseOperand(
        MirOperand operand,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex,
        int tiedToDefIndex)
    {
        if (operand is VirtualReg vreg && tiedToDefIndex >= 0)
            return $"%{vreg.Id}(tied-def {tiedToDefIndex})";
        return FormatOperand(operand, function, blockIndex);
    }

    private string FormatOperand(
        MirOperand operand,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex) => operand switch
    {
        VirtualReg v  => $"%{v.Id}",
        PhysicalReg p => $"${FormatPhysReg(p.Id)}",
        Immediate imm => imm.Value.ToString(),
        BlockTarget bt => FormatBlockTarget(bt, function, blockIndex),
        Symbol s      => $"@{s.Name}",
        _ => throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}"),
    };

    private string FormatBlockTarget(
        BlockTarget target,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex)
    {
        var idx = blockIndex[target.Block];
        if (target.Args.Length == 0)
            return $"bb{idx}";
        var args = string.Join(", ", target.Args.Select(a => FormatOperand(a, function, blockIndex)));
        return $"bb{idx}({args})";
    }

    private static string FormatAnnotation(VRegAnnotation annotation) => annotation switch
    {
        TypedVReg t   => t.Type.DisplayName,
        ClassedVReg c => c.Name,
        _ => throw new InvalidOperationException($"Unknown vreg annotation: {annotation.GetType().Name}"),
    };

    private static string FormatType(IRType type) => type.DisplayName;

    private string FormatPhysReg(int id) => _physRegNamer != null ? _physRegNamer(id) : id.ToString();
}
