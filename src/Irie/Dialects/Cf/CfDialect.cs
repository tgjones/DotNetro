using Irie.Mir;

namespace Irie.Dialects.Cf;

public sealed class CfDialect : Dialect
{
    public static DialectId Id { get; private set; }

    public override string Prefix => "cf";

    public static OpcodeRef OpRef(CfOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((CfOp)code) switch
    {
        CfOp.Br     => "br",
        CfOp.CondBr => "cond_br",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown cf opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "br":      code = (ushort)CfOp.Br;     return true;
            case "cond_br": code = (ushort)CfOp.CondBr; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    public override bool IsSideEffectFree(ushort code) => false;

    public override bool IsTerminator(ushort code) => true;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
