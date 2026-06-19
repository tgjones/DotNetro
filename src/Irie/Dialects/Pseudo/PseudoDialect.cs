using Irie.Mir;

namespace Irie.Dialects.Pseudo;

public sealed class PseudoDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "pseudo";

    public static OpcodeRef OpRef(PseudoOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Copy    => "copy",
        PseudoOp.Merge   => "merge",
        PseudoOp.Unmerge => "unmerge",
        PseudoOp.Extract => "extract",
        PseudoOp.Insert  => "insert",
        PseudoOp.Return  => "return",
        PseudoOp.Spill   => "spill",
        PseudoOp.Reload  => "reload",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown pseudo opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "copy":    code = (ushort)PseudoOp.Copy;    return true;
            case "merge":   code = (ushort)PseudoOp.Merge;   return true;
            case "unmerge": code = (ushort)PseudoOp.Unmerge; return true;
            case "extract": code = (ushort)PseudoOp.Extract; return true;
            case "insert":  code = (ushort)PseudoOp.Insert;  return true;
            case "return":  code = (ushort)PseudoOp.Return;  return true;
            case "spill":   code = (ushort)PseudoOp.Spill;   return true;
            case "reload":  code = (ushort)PseudoOp.Reload;  return true;
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
        PseudoOp.Extract => true,
        PseudoOp.Insert  => true,
        PseudoOp.Return  => false,
        // A reload only writes a register; if that register is unused the reload
        // is dead and may be removed. A spill writes the frame slot (memory) and
        // is therefore observable — never dead.
        PseudoOp.Reload  => true,
        PseudoOp.Spill   => false,
        _ => false,
    };

    public override bool IsTerminator(ushort code) => ((PseudoOp)code) == PseudoOp.Return;

    public override bool IsArtifact(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Merge   => true,
        PseudoOp.Unmerge => true,
        PseudoOp.Extract => true,
        PseudoOp.Insert  => true,
        _ => false,
    };

    // Legal inline immediates: structural attributes (bit offsets, slot indices),
    // not value literals. pseudo.extract's bit offset (use 1), pseudo.insert's bit
    // offset (use 2), pseudo.spill's slot index (use 0, no def), pseudo.reload's
    // slot index (use 0, after the def).
    public override bool IsLegalImmediateOperand(ushort code, int useIndex) =>
        (PseudoOp)code switch
        {
            PseudoOp.Extract => useIndex == 1,
            PseudoOp.Insert  => useIndex == 2,
            PseudoOp.Spill   => useIndex == 0,
            PseudoOp.Reload  => useIndex == 0,
            _ => false,
        };

    internal override void OnRegistered(DialectId id) => Id = id;
}
