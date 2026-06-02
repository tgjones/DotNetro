using Irie.Mir;

namespace Irie.Dialects.Mem;

public sealed class MemDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "mem";

    public static OpcodeRef OpRef(MemOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((MemOp)code) switch
    {
        MemOp.Symbol   => "symbol",
        MemOp.LoadI8   => "load.i8",
        MemOp.LoadI16  => "load.i16",
        MemOp.LoadI32  => "load.i32",
        MemOp.StoreI8  => "store.i8",
        MemOp.StoreI16 => "store.i16",
        MemOp.StoreI32 => "store.i32",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown mem opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "symbol":    code = (ushort)MemOp.Symbol;   return true;
            case "load.i8":   code = (ushort)MemOp.LoadI8;   return true;
            case "load.i16":  code = (ushort)MemOp.LoadI16;  return true;
            case "load.i32":  code = (ushort)MemOp.LoadI32;  return true;
            case "store.i8":  code = (ushort)MemOp.StoreI8;  return true;
            case "store.i16": code = (ushort)MemOp.StoreI16; return true;
            case "store.i32": code = (ushort)MemOp.StoreI32; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    // mem.symbol is pure (returns a constant pointer value). Loads and stores
    // observe / modify memory, so they have side effects.
    public override bool IsSideEffectFree(ushort code) => ((MemOp)code) switch
    {
        MemOp.Symbol => true,
        _ => false,
    };

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
