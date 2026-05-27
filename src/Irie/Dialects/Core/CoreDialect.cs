using Irie.Mir;

namespace Irie.Dialects.Core;

public sealed class CoreDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "core";

    public static OpcodeRef OpRef(CoreOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((CoreOp)code) switch
    {
        CoreOp.Return => "return",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown core opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "return": code = (ushort)CoreOp.Return; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    public override bool IsSideEffectFree(ushort code) => false;

    public override bool IsTerminator(ushort code) => ((CoreOp)code) == CoreOp.Return;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
