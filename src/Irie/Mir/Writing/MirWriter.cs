
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

        foreach (var global in module.Globals)
            WriteGlobal(global, writer);

        var hasGlobals = module.Globals.Count > 0;
        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (i > 0 || hasGlobals) writer.WriteLine();
            WriteFunction(module.Functions[i], writer);
        }
    }

    // Module-level globals: `global @name : <type> [= <initializer>]`.
    // Initializer is omitted for zero-init (`.bss`-style) globals.
    private static void WriteGlobal(MirGlobal global, TextWriter writer)
    {
        writer.Write($"global @{global.SymbolName} : {global.Type.DisplayName}");
        if (global.Initializer is not null)
        {
            writer.Write(" = ");
            WriteInitializer(global.Initializer, writer);
        }
        writer.WriteLine();
    }

    private static void WriteInitializer(MirInitializer initializer, TextWriter writer)
    {
        if (initializer.Items.Length == 1)
        {
            WriteDataItem(initializer.Items[0], writer);
            return;
        }
        writer.Write("[");
        for (var i = 0; i < initializer.Items.Length; i++)
        {
            if (i > 0) writer.Write(", ");
            WriteDataItem(initializer.Items[i], writer);
        }
        writer.Write("]");
    }

    private static void WriteDataItem(MirDataItem item, TextWriter writer)
    {
        switch (item)
        {
            case DataBytes(var bytes):
                writer.Write("bytes \"");
                foreach (var b in bytes)
                {
                    switch (b)
                    {
                        case (byte)'\\': writer.Write(@"\\"); break;
                        case (byte)'"':  writer.Write("\\\""); break;
                        case (byte)'\n': writer.Write(@"\n"); break;
                        case (byte)'\r': writer.Write(@"\r"); break;
                        case (byte)'\t': writer.Write(@"\t"); break;
                        case >= 0x20 and < 0x7F:
                            writer.Write((char)b);
                            break;
                        default:
                            writer.Write($"\\x{b:X2}");
                            break;
                    }
                }
                writer.Write("\"");
                break;
            case DataSymbolRef(var name):
                writer.Write($"@{name}");
                break;
            default:
                throw new InvalidOperationException($"Unknown MirDataItem: {item.GetType().Name}");
        }
    }

    private void WriteFunction(MirFunction function, TextWriter writer)
    {
        var paramStr = string.Join(", ", function.ParameterTypes.Select(FormatType));
        writer.WriteLine($"func @{function.Name} : ({paramStr}) -> {FormatType(function.ReturnType)} {{");

        foreach (var slot in function.FrameSlots)
        {
            writer.Write($"  frame_slot {slot.Index} : {FormatType(slot.Type)} @{slot.SymbolName}");
            // A non-default placement (stamped by a target's post-RA frame-placement
            // pass) is printed so the decision is visible in post-pass MIR dumps.
            if (slot.StackId != FrameSlot.DefaultStackId)
                writer.Write($" stackid {slot.StackId} offset {slot.Offset}");
            writer.WriteLine();
        }

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
        var useIndex = 0;
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
                useStrings.Add(FormatUseOperand(op, function, blockIndex, tiedTo, dialect, instruction.Opcode.Code, useIndex));
                useIndex++;
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
    // dialect's TiedOperands metadata. Immediate uses can opt into symbolic
    // rendering via Dialect.TryFormatImmediateUse (e.g. arith.cmpi's predicate).
    private string FormatUseOperand(
        MirOperand operand,
        MirFunction function,
        Dictionary<MirBlock, int> blockIndex,
        int tiedToDefIndex,
        Dialect dialect,
        ushort opcode,
        int useIndex)
    {
        if (operand is VirtualReg vreg && tiedToDefIndex >= 0)
            return $"%{vreg.Id}(tied-def {tiedToDefIndex})";
        if (operand is Immediate imm
            && dialect.TryFormatImmediateUse(opcode, useIndex, imm.Value, out var symbolic))
            return symbolic;
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
