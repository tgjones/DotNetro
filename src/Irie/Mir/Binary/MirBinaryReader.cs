using static Irie.Mir.Binary.MirBinaryFormat;

namespace Irie.Mir.Binary;

internal static class MirBinaryReader
{
    public static MirModule Read(BinaryReader reader)
    {
        MirBootstrap.EnsureRegistered();

        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a MIR binary file: magic header mismatch.");

        var version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported MIR binary version: {version} (expected {FormatVersion}).");

        // The file's dialect table is indexed by file position; we remap each
        // entry to its runtime DialectId by prefix lookup.
        var dialectCount = reader.ReadInt32();
        var dialectMap = new DialectId[dialectCount];
        for (var i = 0; i < dialectCount; i++)
        {
            var prefix = reader.ReadString();
            if (!DialectRegistry.TryByPrefix(prefix, out var dialect))
                throw new InvalidDataException($"Unknown dialect '{prefix}' referenced in MIR binary.");
            dialectMap[i] = dialect.Id;
        }

        var module = new MirModule();

        var globalCount = reader.ReadInt32();
        for (var i = 0; i < globalCount; i++)
            module.Globals.Add(ReadGlobal(reader));

        var functionCount = reader.ReadInt32();
        for (var i = 0; i < functionCount; i++)
            module.Functions.Add(ReadFunction(reader, dialectMap));
        return module;
    }

    private static MirGlobal ReadGlobal(BinaryReader reader)
    {
        var name = reader.ReadString();
        var type = ReadType(reader);
        var size = reader.ReadInt32();
        var hasInitializer = reader.ReadBoolean();
        MirInitializer? initializer = null;
        if (hasInitializer)
        {
            var itemCount = reader.ReadInt32();
            var items = new MirDataItem[itemCount];
            for (var i = 0; i < itemCount; i++)
                items[i] = ReadDataItem(reader);
            initializer = new MirInitializer(items);
        }
        return new MirGlobal(name, type, size, initializer);
    }

    private static MirDataItem ReadDataItem(BinaryReader reader)
    {
        var tag = (DataItemTag)reader.ReadByte();
        return tag switch
        {
            DataItemTag.Bytes     => new DataBytes(reader.ReadBytes(reader.ReadInt32())),
            DataItemTag.SymbolRef => new DataSymbolRef(reader.ReadString()),
            _ => throw new InvalidDataException($"Unknown data item tag: {(byte)tag}"),
        };
    }

    private static MirFunction ReadFunction(BinaryReader reader, DialectId[] dialectMap)
    {
        var name = reader.ReadString();

        var paramCount = reader.ReadInt32();
        var paramTypes = new IRType[paramCount];
        for (var i = 0; i < paramCount; i++)
            paramTypes[i] = ReadType(reader);
        var returnType = ReadType(reader);

        var function = new MirFunction(name, paramTypes, returnType);

        var frameSlotCount = reader.ReadInt32();
        for (var i = 0; i < frameSlotCount; i++)
        {
            var slotIndex = reader.ReadInt32();
            var slotType = ReadType(reader);
            var slotName = reader.ReadString();
            function.FrameSlots.Add(new FrameSlot(slotIndex, slotType, slotName));
        }

        var vregCount = reader.ReadInt32();
        for (var i = 0; i < vregCount; i++)
        {
            var id = reader.ReadInt32();
            var annotation = ReadAnnotation(reader);
            function.RegisterVirtualRegister(id, annotation);
        }

        var blockCount = reader.ReadInt32();
        // Pre-create all blocks so terminators with forward BlockTarget refs
        // can resolve against the function's block list during instruction
        // decoding.
        for (var i = 0; i < blockCount; i++)
            function.CreateBlock();

        for (var i = 0; i < blockCount; i++)
            ReadBlock(reader, function, function.Blocks[i], dialectMap);

        return function;
    }

    private static void ReadBlock(BinaryReader reader, MirFunction function, MirBlock block, DialectId[] dialectMap)
    {
        var paramCount = reader.ReadInt32();
        for (var i = 0; i < paramCount; i++)
            block.Parameters.Add(reader.ReadInt32());

        var liveInCount = reader.ReadInt32();
        for (var i = 0; i < liveInCount; i++)
            block.LiveIns.Add(reader.ReadInt32());

        var instrCount = reader.ReadInt32();
        for (var i = 0; i < instrCount; i++)
        {
            var fileDialectIndex = reader.ReadInt32();
            if ((uint)fileDialectIndex >= (uint)dialectMap.Length)
                throw new InvalidDataException($"Invalid dialect index {fileDialectIndex} in instruction stream.");
            var code = reader.ReadUInt16();
            var opcode = new OpcodeRef(dialectMap[fileDialectIndex], code);

            var operandCount = reader.ReadInt32();
            var operands = new MirOperand[operandCount];
            for (var j = 0; j < operandCount; j++)
                operands[j] = ReadOperand(reader, function);

            block.AddInstruction(opcode, operands);
        }
    }

    private static MirOperand ReadOperand(BinaryReader reader, MirFunction function)
    {
        var tag = (OperandTag)reader.ReadByte();
        return tag switch
        {
            OperandTag.VirtualReg  => new VirtualReg(reader.ReadInt32(), reader.ReadBoolean()),
            OperandTag.PhysicalReg => new PhysicalReg(reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean()),
            OperandTag.Immediate   => new Immediate(reader.ReadInt64()),
            OperandTag.BlockTarget => ReadBlockTarget(reader, function),
            OperandTag.Symbol      => new Symbol(reader.ReadString(), reader.ReadInt32()),
            _ => throw new InvalidDataException($"Unknown operand tag: {(byte)tag}"),
        };
    }

    private static BlockTarget ReadBlockTarget(BinaryReader reader, MirFunction function)
    {
        var blockIdx = reader.ReadInt32();
        if ((uint)blockIdx >= (uint)function.Blocks.Count)
            throw new InvalidDataException($"Invalid block index {blockIdx} in BlockTarget.");
        var argCount = reader.ReadInt32();
        var args = new MirOperand[argCount];
        for (var i = 0; i < argCount; i++)
            args[i] = ReadOperand(reader, function);
        return new BlockTarget(function.Blocks[blockIdx], args);
    }

    private static VRegAnnotation ReadAnnotation(BinaryReader reader)
    {
        var tag = (VRegAnnotationTag)reader.ReadByte();
        return tag switch
        {
            VRegAnnotationTag.Typed   => new TypedVReg(ReadType(reader)),
            VRegAnnotationTag.Classed => new ClassedVReg(reader.ReadInt32(), reader.ReadString()),
            _ => throw new InvalidDataException($"Unknown vreg annotation tag: {(byte)tag}"),
        };
    }

    private static IRType ReadType(BinaryReader reader)
    {
        var tag = (TypeTag)reader.ReadByte();
        return tag switch
        {
            TypeTag.Void => IRType.Void,
            TypeTag.I1   => IRType.I1,
            TypeTag.I8   => IRType.I8,
            TypeTag.I16  => IRType.I16,
            TypeTag.I32  => IRType.I32,
            _ => throw new InvalidDataException($"Unknown type tag: {(byte)tag}"),
        };
    }
}
