using Irie.Mir;

namespace Irie.Dialects.Pseudo;

public sealed class PseudoDialect : Dialect
{
    public static DialectId Id { get; private set; }

    public override string Prefix => "pseudo";

    public static OpcodeRef OpRef(PseudoOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Copy    => "copy",
        PseudoOp.Merge   => "merge",
        PseudoOp.Unmerge => "unmerge",
        PseudoOp.Return  => "return",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown pseudo opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "copy":    code = (ushort)PseudoOp.Copy;    return true;
            case "merge":   code = (ushort)PseudoOp.Merge;   return true;
            case "unmerge": code = (ushort)PseudoOp.Unmerge; return true;
            case "return":  code = (ushort)PseudoOp.Return;  return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    public override bool IsSideEffectFree(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Copy    => true,
        PseudoOp.Merge   => true,
        PseudoOp.Unmerge => true,
        PseudoOp.Return  => false,
        _ => false,
    };

    public override bool IsTerminator(ushort code) => ((PseudoOp)code) == PseudoOp.Return;

    public override bool IsArtifact(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Merge   => true,
        PseudoOp.Unmerge => true,
        _ => false,
    };

    internal override void OnRegistered(DialectId id) => Id = id;
}
