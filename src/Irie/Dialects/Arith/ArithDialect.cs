using Irie.Mir;

namespace Irie.Dialects.Arith;

public sealed class ArithDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "arith";

    public static OpcodeRef OpRef(ArithOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((ArithOp)code) switch
    {
        ArithOp.AddI       => "addi",
        ArithOp.SubI       => "subi",
        ArithOp.CmpI       => "cmpi",
        ArithOp.AddICarry  => "addi_with_carry",
        ArithOp.SubIBorrow => "subi_with_borrow",
        ArithOp.Constant   => "constant",
        ArithOp.Select     => "select",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown arith opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "addi":              code = (ushort)ArithOp.AddI;       return true;
            case "subi":              code = (ushort)ArithOp.SubI;       return true;
            case "cmpi":              code = (ushort)ArithOp.CmpI;       return true;
            case "addi_with_carry":   code = (ushort)ArithOp.AddICarry;  return true;
            case "subi_with_borrow":  code = (ushort)ArithOp.SubIBorrow; return true;
            case "constant":          code = (ushort)ArithOp.Constant;   return true;
            case "select":            code = (ushort)ArithOp.Select;     return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        (ArithOp)code switch
        {
            // cmpi's def is always i1, but its legalization decision is driven
            // by the operand type. Operand layout:
            //   [0]=def i1, [1]=predicate Immediate, [2]=a, [3]=b.
            // Point the legalizer at operand[2] (the first arg vreg).
            ArithOp.CmpI => new DialectInstructionInfo(TypeOperandIndex: 2),
            _ => DialectInstructionInfo.Empty,
        };

    public override bool IsSideEffectFree(ushort code) => true;

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    // arith.cmpi's first use is the predicate (encoded as an Immediate with the
    // ArithCmpPredicate enum value); render it symbolically (`eq`, `slt`, …)
    // rather than as a number.
    public override bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        if ((ArithOp)code == ArithOp.CmpI && useIndex == 0)
        {
            text = ArithCmpPredicateNames.ToText((ArithCmpPredicate)value);
            return true;
        }
        text = string.Empty;
        return false;
    }

    public override bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        if ((ArithOp)code == ArithOp.CmpI && useIndex == 0
            && ArithCmpPredicateNames.TryFromText(text, out var predicate))
        {
            value = (long)predicate;
            return true;
        }
        value = 0;
        return false;
    }

    internal override void OnRegistered(DialectId id) => Id = id;
}
