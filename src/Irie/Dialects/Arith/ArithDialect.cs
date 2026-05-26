using Irie.Mir;

namespace Irie.Dialects.Arith;

public sealed class ArithDialect : Dialect
{
    public static DialectId Id { get; private set; }

    public override string Prefix => "arith";

    public static OpcodeRef OpRef(ArithOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((ArithOp)code) switch
    {
        ArithOp.AddI      => "addi",
        ArithOp.SubI      => "subi",
        ArithOp.CmpI      => "cmpi",
        ArithOp.AddICarry => "addi_with_carry",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown arith opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "addi":             code = (ushort)ArithOp.AddI;      return true;
            case "subi":             code = (ushort)ArithOp.SubI;      return true;
            case "cmpi":             code = (ushort)ArithOp.CmpI;      return true;
            case "addi_with_carry":  code = (ushort)ArithOp.AddICarry; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    public override bool IsSideEffectFree(ushort code) => true;

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
