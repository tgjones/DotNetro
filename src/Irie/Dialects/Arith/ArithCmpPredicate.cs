namespace Irie.Dialects.Arith;

// Predicate kinds for arith.cmpi. Encoding (embedded immediate vs. opcode
// subkind) is deferred to step 7/8 work — see notes/unified-ir-plan.md
// open questions #1.
public enum ArithCmpPredicate
{
    Eq,
    Ne,
    Slt,
    Sgt,
    Sle,
    Sge,
    Ult,
    Ugt,
    Ule,
    Uge,
}
