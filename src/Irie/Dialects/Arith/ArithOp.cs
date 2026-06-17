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

    // %r, %bout = arith.subi_with_borrow %a, %b, %bin
    // Post-legalization narrowed-sub form. borrow_in / borrow_out use 6502
    // C-flag polarity: 1 = no borrow (carry set), 0 = borrow (carry clear).
    // So the chain head's borrow-in is materialized as `arith.constant 1 : i1`
    // (SEC), symmetric with addi_with_carry whose head is `arith.constant 0 : i1`
    // (CLC). Same 5-operand shape as addi_with_carry.
    SubIBorrow,

    // %r = arith.constant <value>
    // Typed integer constant. Used by the legalizer to materialize the
    // chain-head carry-in `arith.constant 0 : i1` so every addi_with_carry
    // has a uniform 3-use shape.
    Constant,

    // %r = arith.select %cond, %a, %b
    // SSA conditional value: %r is %a when %cond (i1) is true, else %b. Both value
    // operands share %r's type. A materialized (value) select is lowered to a CFG
    // diamond by the target's pre-isel select-lowering pass (mirrors llvm-mos
    // MOSLowerSelect); the i1 selects of a wide-compare lexicographic tree are
    // re-fused into a CMP+branch ladder by the instruction selector.
    Select,

    // %r = arith.not %x   — logical negation of an i1 (mirrors llvm-mos buildNot).
    // Emitted by the legalizer's consumer-independent predicate normalization
    // (ne/ult/sge/… → eq/uge/slt + NOT). It is never materialized: the instruction
    // selector strips it at the consuming cf.cond_br by swapping the branch's
    // true/false targets.
    Not,
}
