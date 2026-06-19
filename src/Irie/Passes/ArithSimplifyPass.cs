using Irie.Dialects.Arith;
using Irie.Mir;

namespace Irie.Passes;

// Generic mid-level arithmetic simplification on generic-dialect SSA MIR — the
// scoped analogue of LLVM's InstCombine/Reassociate for the add/sub family.
//
// llvm-mos folds e.g. `(a + 1) + (b - 1)` to `a + b` in its target-independent
// InstCombine pass, long before any 6502 lowering (see the inc-dec corpus .txt:
// the whole win lands in InstCombinePass). Irie had no mid-level optimization
// layer at all, so the `+1`/`-1` survived to isel as real ALU ops. This pass
// closes that gap: it linearizes a tree of `arith.addi`/`arith.subi` into a flat
// sum of signed leaves plus a net constant, then rebuilds the minimal form.
//
// The transferable lesson from InstCombine's monolithic design is the
// worklist-to-fixpoint shape, not the grab-bag of rules: when a rewrite lands,
// re-enqueue the instructions that now use the new value, so an enabled outer
// fold runs in the same pass. There is currently exactly one rule (add/sub
// reassociation); a second rule would slot into the same worklist rather than a
// separate pass (so they share the traversal and a consistent canonical form).
//
// Soundness: i8/i16/i32 two's-complement add/sub is associative and commutative
// under modular wrap, and Irie's generic MIR carries no nsw/poison semantics, so
// reassociating and constant-folding is unconditionally value-preserving. The
// pass only ever rewrites when it strictly reduces the add/sub instruction count,
// which also guarantees termination.
//
// Runs early, on generic SSA after the verifier (which guarantees value operands
// are vregs, never inline immediates — constants are always `arith.constant`
// defs) and before ABI lowering. It only touches the generic arith dialect, so it
// is a no-op on hand-written target-dialect `--start-at` inputs.
public sealed class ArithSimplifyPass : MirFunctionPass
{
    public override string Name => "ArithSimplify";

    private readonly record struct Term(int Vreg, bool Negated);

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);

        var worklist = new Queue<MirInstruction>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                if (IsAddSub(instr))
                    worklist.Enqueue(instr);

        while (worklist.Count > 0)
        {
            var instr = worklist.Dequeue();
            if (instr.Parent is null || !IsAddSub(instr)) continue; // already folded away
            if (TrySimplify(function, builder, instr, out var newResult))
                foreach (var user in UsersOf(function, newResult))
                    if (IsAddSub(user))
                        worklist.Enqueue(user);
        }

        // The folded-away root, the absorbed single-use intermediates, and any now
        // unused shared constant defs are all trivially dead — sweep them.
        RemoveDeadArith(function, builder);
    }

    private static bool IsAddSub(MirInstruction instr) =>
        instr.Opcode.Dialect == ArithDialect.Id &&
        ((ArithOp)instr.Opcode.Code is ArithOp.AddI or ArithOp.SubI);

    // Linearize the add/sub tree rooted at `instr` and, if the rebuilt form uses
    // strictly fewer add/sub ops, build it and redirect the root's uses to it.
    private bool TrySimplify(MirFunction function, MirBuilder builder, MirInstruction instr, out int newResult)
    {
        newResult = -1;

        if (instr.Operands is not [VirtualReg { IsDefinition: true } resultDef,
                                   VirtualReg aUse, VirtualReg bUse])
            return false;

        if (function.GetVRegAnnotation(resultDef.Id) is not TypedVReg { Type: IntegerType intType })
            return false;

        var terms = new List<Term>();
        long constant = 0;
        var absorbed = 0;

        var rootIsAdd = (ArithOp)instr.Opcode.Code == ArithOp.AddI;
        Flatten(function, aUse.Id, negated: false, terms, ref constant, ref absorbed);
        Flatten(function, bUse.Id, negated: !rootIsAdd, terms, ref constant, ref absorbed);

        var bits = intType.SizeInBits;
        var mask = bits >= 64 ? ~0L : (1L << bits) - 1;
        var foldedConstant = constant & mask;

        // Cost: the original tree is the root plus every absorbed intermediate. The
        // rebuilt form is one op per surviving term after the first, plus a
        // constant materialization + add when the net constant is non-zero.
        var newOps = (terms.Count - 1) + (foldedConstant != 0 ? 2 : 0);
        var originalOps = 1 + absorbed;
        if (newOps >= originalOps) return false;

        // Need a positive leaf to anchor the chain — bail rather than synthesize a
        // unary negate (no such op, and this case does not arise from add/sub trees
        // whose leftmost spine always enters positive).
        var anchorIndex = terms.FindIndex(static t => !t.Negated);
        if (anchorIndex < 0) return false;

        builder.SetInsertionPointBefore(instr);

        var acc = terms[anchorIndex].Vreg;
        terms.RemoveAt(anchorIndex);
        foreach (var term in terms)
            acc = BuildBinary(function, builder, term.Negated ? ArithOp.SubI : ArithOp.AddI, intType, acc, term.Vreg);

        if (foldedConstant != 0)
        {
            var constVreg = builder.BuildConstant(intType, foldedConstant);
            acc = BuildBinary(function, builder, ArithOp.AddI, intType, acc, constVreg);
        }

        function.ReplaceAllUsesOfRegister(resultDef.Id, acc);
        newResult = acc;
        return true;
    }

    // Walk the add/sub tree, absorbing single-use intermediate add/sub nodes and
    // folding constant leaves into `constant`; everything else is a leaf term.
    private static void Flatten(MirFunction function, int vreg, bool negated,
                                List<Term> terms, ref long constant, ref int absorbed)
    {
        var def = function.GetDefinition(vreg);

        if (def is not null && def.Opcode.Dialect == ArithDialect.Id)
        {
            switch ((ArithOp)def.Opcode.Code)
            {
                case ArithOp.Constant when def.Operands is [_, Immediate imm]:
                    constant += negated ? -imm.Value : imm.Value;
                    return;

                // Only absorb an intermediate that is used solely by this tree; a
                // shared (multi-use) node must stay put and is treated as a leaf.
                case ArithOp.AddI when function.GetUseCount(vreg) == 1 &&
                                       def.Operands is [_, VirtualReg a1, VirtualReg b1]:
                    absorbed++;
                    Flatten(function, a1.Id, negated, terms, ref constant, ref absorbed);
                    Flatten(function, b1.Id, negated, terms, ref constant, ref absorbed);
                    return;

                case ArithOp.SubI when function.GetUseCount(vreg) == 1 &&
                                       def.Operands is [_, VirtualReg a2, VirtualReg b2]:
                    absorbed++;
                    Flatten(function, a2.Id, negated, terms, ref constant, ref absorbed);
                    Flatten(function, b2.Id, !negated, terms, ref constant, ref absorbed);
                    return;
            }
        }

        terms.Add(new Term(vreg, negated));
    }

    private static int BuildBinary(MirFunction function, MirBuilder builder, ArithOp op, IRType type, int a, int b)
    {
        var result = function.CreateVirtualRegister(type);
        builder.BuildInstruction(ArithDialect.OpRef(op), [
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(a, IsDefinition: false),
            new VirtualReg(b, IsDefinition: false),
        ]);
        return result;
    }

    private static IEnumerable<MirInstruction> UsersOf(MirFunction function, int vreg)
    {
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualReg { IsDefinition: false } v && v.Id == vreg)
                    {
                        yield return instr;
                        break;
                    }
    }

    private static void RemoveDeadArith(MirFunction function, MirBuilder builder)
    {
        bool removedAny;
        do
        {
            removedAny = false;
            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions.ToArray())
                    if (instr.Parent is not null
                        && instr.Opcode.Dialect == ArithDialect.Id
                        && function.IsTriviallyDead(instr))
                    {
                        builder.Remove(instr);
                        removedAny = true;
                    }
        } while (removedAny);
    }
}
