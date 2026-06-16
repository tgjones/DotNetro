using static Irie.Mir.Binary.MirBinaryFormat;

namespace Irie.Mir.Binary;

internal static class MirBinaryWriter
{
    public static void Write(MirModule module, BinaryWriter writer)
    {
        MirBootstrap.EnsureRegistered();

        writer.Write(Magic);
        writer.Write(FormatVersion);

        // The file's dialect table makes the binary self-describing: each
        // OpcodeRef is encoded with the table index, and the reader remaps
        // those to the runtime DialectIds (which depend on registration order
        // in the consuming process).
        var dialectTable = BuildDialectTable(module);
        writer.Write(dialectTable.Count);
        foreach (var prefix in dialectTable.Keys)
            writer.Write(prefix);

        writer.Write(module.Globals.Count);
        foreach (var global in module.Globals)
            WriteGlobal(global, writer);

        writer.Write(module.Functions.Count);
        foreach (var function in module.Functions)
            WriteFunction(function, writer, dialectTable);
    }

    private static void WriteGlobal(MirGlobal global, BinaryWriter writer)
    {
        writer.Write(global.SymbolName);
        WriteType(global.Type, writer);
        writer.Write(global.SizeInBytes);
        writer.Write(global.Initializer is not null);
        if (global.Initializer is not null)
        {
            writer.Write(global.Initializer.Items.Length);
            foreach (var item in global.Initializer.Items)
                WriteDataItem(item, writer);
        }
    }

    private static void WriteDataItem(MirDataItem item, BinaryWriter writer)
    {
        switch (item)
        {
            case DataBytes(var bytes):
                writer.Write((byte)DataItemTag.Bytes);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            case DataSymbolRef(var name):
                writer.Write((byte)DataItemTag.SymbolRef);
                writer.Write(name);
                break;
            default:
                throw new InvalidOperationException($"Unknown MirDataItem: {item.GetType().Name}");
        }
    }

    private static Dictionary<string, int> BuildDialectTable(MirModule module)
    {
        var table = new Dictionary<string, int>();
        foreach (var function in module.Functions)
            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions)
                {
                    var prefix = DialectRegistry.ById(instr.Opcode.Dialect).Prefix;
                    if (!table.ContainsKey(prefix))
                        table[prefix] = table.Count;
                }
        return table;
    }

    private static void WriteFunction(MirFunction function, BinaryWriter writer, Dictionary<string, int> dialectTable)
    {
        writer.Write(function.Name);

        writer.Write(function.ParameterTypes.Length);
        foreach (var type in function.ParameterTypes)
            WriteType(type, writer);
        WriteType(function.ReturnType, writer);

        writer.Write(function.FrameSlots.Count);
        foreach (var slot in function.FrameSlots)
        {
            writer.Write(slot.Index);
            WriteType(slot.Type, writer);
            writer.Write(slot.SymbolName);
        }

        var vregIds = function.VirtualRegisterIds;
        writer.Write(vregIds.Count);
        foreach (var id in vregIds)
        {
            writer.Write(id);
            WriteAnnotation(function.GetVRegAnnotation(id), writer);
        }

        var blockIndex = new Dictionary<MirBlock, int>();
        for (var i = 0; i < function.Blocks.Count; i++)
            blockIndex[function.Blocks[i]] = i;

        writer.Write(function.Blocks.Count);
        foreach (var block in function.Blocks)
            WriteBlock(block, writer, dialectTable, blockIndex);
    }

    private static void WriteBlock(
        MirBlock block,
        BinaryWriter writer,
        Dictionary<string, int> dialectTable,
        Dictionary<MirBlock, int> blockIndex)
    {
        writer.Write(block.Parameters.Count);
        foreach (var vreg in block.Parameters)
            writer.Write(vreg);

        writer.Write(block.LiveIns.Count);
        foreach (var physReg in block.LiveIns)
            writer.Write(physReg);

        writer.Write(block.Instructions.Count);
        foreach (var instr in block.Instructions)
            WriteInstruction(instr, writer, dialectTable, blockIndex);
    }

    private static void WriteInstruction(
        MirInstruction instr,
        BinaryWriter writer,
        Dictionary<string, int> dialectTable,
        Dictionary<MirBlock, int> blockIndex)
    {
        var prefix = DialectRegistry.ById(instr.Opcode.Dialect).Prefix;
        writer.Write(dialectTable[prefix]);
        writer.Write(instr.Opcode.Code);

        writer.Write(instr.Operands.Length);
        foreach (var operand in instr.Operands)
            WriteOperand(operand, writer, blockIndex);
    }

    private static void WriteOperand(MirOperand operand, BinaryWriter writer, Dictionary<MirBlock, int> blockIndex)
    {
        switch (operand)
        {
            case VirtualReg v:
                writer.Write((byte)OperandTag.VirtualReg);
                writer.Write(v.Id);
                writer.Write(v.IsDefinition);
                break;

            case PhysicalReg p:
                writer.Write((byte)OperandTag.PhysicalReg);
                writer.Write(p.Id);
                writer.Write(p.IsDefinition);
                writer.Write(p.IsImplicit);
                break;

            case Immediate imm:
                writer.Write((byte)OperandTag.Immediate);
                writer.Write(imm.Value);
                break;

            case BlockTarget bt:
                writer.Write((byte)OperandTag.BlockTarget);
                writer.Write(blockIndex[bt.Block]);
                writer.Write(bt.Args.Length);
                foreach (var arg in bt.Args)
                    WriteOperand(arg, writer, blockIndex);
                break;

            case Symbol s:
                writer.Write((byte)OperandTag.Symbol);
                writer.Write(s.Name);
                writer.Write(s.Offset);
                break;

            default:
                throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}");
        }
    }

    private static void WriteAnnotation(VRegAnnotation annotation, BinaryWriter writer)
    {
        switch (annotation)
        {
            case TypedVReg t:
                writer.Write((byte)VRegAnnotationTag.Typed);
                WriteType(t.Type, writer);
                break;

            case ClassedVReg c:
                writer.Write((byte)VRegAnnotationTag.Classed);
                writer.Write(c.ClassId);
                writer.Write(c.Name);
                break;

            default:
                throw new InvalidOperationException($"Unknown vreg annotation: {annotation.GetType().Name}");
        }
    }

    private static void WriteType(IRType type, BinaryWriter writer)
    {
        writer.Write((byte)(type switch
        {
            VoidType                       => TypeTag.Void,
            IntegerType { SizeInBits: 1 }  => TypeTag.I1,
            IntegerType { SizeInBits: 8 }  => TypeTag.I8,
            IntegerType { SizeInBits: 16 } => TypeTag.I16,
            IntegerType { SizeInBits: 32 } => TypeTag.I32,
            _ => throw new InvalidOperationException($"Cannot serialize type: {type.DisplayName}"),
        }));
    }
}
