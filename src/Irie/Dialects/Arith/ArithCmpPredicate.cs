namespace Irie.Dialects.Arith;

// Predicate kinds for arith.cmpi. Encoded in MIR as an Immediate operand at
// use position 0 of `arith.cmpi`; rendered symbolically by the writer/parser
// via ArithDialect.TryFormat/ParseImmediateUse. The integer values are the
// canonical encoding for the binary format.
public enum ArithCmpPredicate
{
    Eq  = 0,
    Ne  = 1,
    Slt = 2,
    Sgt = 3,
    Sle = 4,
    Sge = 5,
    Ult = 6,
    Ugt = 7,
    Ule = 8,
    Uge = 9,
}

public static class ArithCmpPredicateOps
{
    // The inverse (logical negation) of a predicate: the relation that holds
    // exactly when this one does not. eq↔ne, slt↔sge, sgt↔sle, ult↔uge, ugt↔ule.
    public static ArithCmpPredicate Inverse(ArithCmpPredicate predicate) => predicate switch
    {
        ArithCmpPredicate.Eq  => ArithCmpPredicate.Ne,
        ArithCmpPredicate.Ne  => ArithCmpPredicate.Eq,
        ArithCmpPredicate.Slt => ArithCmpPredicate.Sge,
        ArithCmpPredicate.Sge => ArithCmpPredicate.Slt,
        ArithCmpPredicate.Sgt => ArithCmpPredicate.Sle,
        ArithCmpPredicate.Sle => ArithCmpPredicate.Sgt,
        ArithCmpPredicate.Ult => ArithCmpPredicate.Uge,
        ArithCmpPredicate.Uge => ArithCmpPredicate.Ult,
        ArithCmpPredicate.Ugt => ArithCmpPredicate.Ule,
        ArithCmpPredicate.Ule => ArithCmpPredicate.Ugt,
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "Unknown ArithCmpPredicate"),
    };

    // The predicate that holds for swapped operands: `a P b` ⟺ `b Swapped(P) a`.
    // eq/ne are symmetric; the orderings reverse (slt↔sgt, sle↔sge, ult↔ugt, ule↔uge).
    public static ArithCmpPredicate Swapped(ArithCmpPredicate predicate) => predicate switch
    {
        ArithCmpPredicate.Eq  => ArithCmpPredicate.Eq,
        ArithCmpPredicate.Ne  => ArithCmpPredicate.Ne,
        ArithCmpPredicate.Slt => ArithCmpPredicate.Sgt,
        ArithCmpPredicate.Sgt => ArithCmpPredicate.Slt,
        ArithCmpPredicate.Sle => ArithCmpPredicate.Sge,
        ArithCmpPredicate.Sge => ArithCmpPredicate.Sle,
        ArithCmpPredicate.Ult => ArithCmpPredicate.Ugt,
        ArithCmpPredicate.Ugt => ArithCmpPredicate.Ult,
        ArithCmpPredicate.Ule => ArithCmpPredicate.Uge,
        ArithCmpPredicate.Uge => ArithCmpPredicate.Ule,
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "Unknown ArithCmpPredicate"),
    };
}

public static class ArithCmpPredicateNames
{
    public static string ToText(ArithCmpPredicate predicate) => predicate switch
    {
        ArithCmpPredicate.Eq  => "eq",
        ArithCmpPredicate.Ne  => "ne",
        ArithCmpPredicate.Slt => "slt",
        ArithCmpPredicate.Sgt => "sgt",
        ArithCmpPredicate.Sle => "sle",
        ArithCmpPredicate.Sge => "sge",
        ArithCmpPredicate.Ult => "ult",
        ArithCmpPredicate.Ugt => "ugt",
        ArithCmpPredicate.Ule => "ule",
        ArithCmpPredicate.Uge => "uge",
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "Unknown ArithCmpPredicate"),
    };

    public static bool TryFromText(string text, out ArithCmpPredicate predicate)
    {
        switch (text)
        {
            case "eq":  predicate = ArithCmpPredicate.Eq;  return true;
            case "ne":  predicate = ArithCmpPredicate.Ne;  return true;
            case "slt": predicate = ArithCmpPredicate.Slt; return true;
            case "sgt": predicate = ArithCmpPredicate.Sgt; return true;
            case "sle": predicate = ArithCmpPredicate.Sle; return true;
            case "sge": predicate = ArithCmpPredicate.Sge; return true;
            case "ult": predicate = ArithCmpPredicate.Ult; return true;
            case "ugt": predicate = ArithCmpPredicate.Ugt; return true;
            case "ule": predicate = ArithCmpPredicate.Ule; return true;
            case "uge": predicate = ArithCmpPredicate.Uge; return true;
        }
        predicate = default;
        return false;
    }
}
