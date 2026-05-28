namespace Irie.Dialects.Arith;

public enum ArithOp : ushort
{
    // %r = arith.addi %a, %b
    AddI,

    // %r = arith.subi %a, %b
    SubI,

    // %cond = arith.cmpi <pred>, %a, %b   (predicate encoding TBD — see
    // notes/unified-ir-plan.md open questions #1)
    CmpI,

    // %r, %cout = arith.addi_with_carry %a, %b, %cin
    // Post-legalization narrowed-add form (replaces today's GenericAddCarry).
    AddICarry,

    // %r = arith.constant <value>
    // Typed integer constant. Used by the legalizer to materialize the
    // chain-head carry-in `arith.constant 0 : i1` so every addi_with_carry
    // has a uniform 3-use shape.
    Constant,
}
