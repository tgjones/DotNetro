using Irie.Mir;

namespace Irie.Dialects.Call;

public sealed class CallDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "call";

    public static OpcodeRef OpRef(CallOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((CallOp)code) switch
    {
        CallOp.Func     => "func",
        CallOp.Indirect => "indirect",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown call opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "func":     code = (ushort)CallOp.Func;     return true;
            case "indirect": code = (ushort)CallOp.Indirect; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    // call ops have side effects (the callee is observable).
    public override bool IsSideEffectFree(ushort code) => false;

    // Calls return to the next instruction; not terminators.
    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
