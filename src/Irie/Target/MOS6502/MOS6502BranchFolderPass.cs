using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private branch-folder — the llvm-mos `branch-folder`
// (BranchFolder::OptimizeBlock, the "empty block" arm) analogue. Runs AFTER
// register allocation and BEFORE CopyElimination, mirroring llvm-mos which runs
// the Control Flow Optimizer after the VReg rewriter and before copy-opt /
// pseudo expansion.
//
// An "empty block" in our post-RA / post-PhiElimination model is a block whose
// only instruction is an unconditional `mos6502.jmp.abs <succ>`. Such a block
// does nothing but transfer control to a single successor, so we make every
// predecessor reach that successor directly (rewriting the BlockTarget operand
// that names the empty block) and then drop the empty block.
//
// llvm-mos: "If this block is empty, make everyone use its fall-through, not the
// block explicitly" — it walks the empty block's predecessors and calls
// `ReplaceUsesOfBlockWith(MBB, FallThrough)`. We do the same by rewriting every
// `BlockTarget` operand across the function that points at the empty block.
//
// Correctness: conditional branches in our model are NOT terminators — they fall
// through to the next physically-laid-out block on the not-taken path. So an
// empty block may only be physically removed from the block list when nothing
// falls through into it (i.e. the physically-preceding block ends in a
// terminator). If a fall-through reaches it we conservatively leave the block in
// place; the explicit predecessors have still been redirected, and the
// later block-placement pass handles the residual jmp.
public sealed class MOS6502BranchFolderPass : MirFunctionPass
{
    public override string Name => "MOS6502BranchFolder";

    public override void Run(MirFunction function)
    {
        bool changed;
        do
        {
            changed = false;
            // Never fold the entry block (index 0) — it is the function entry and
            // carries the function's calling-convention live-ins.
            for (var i = 1; i < function.Blocks.Count; i++)
            {
                var block = function.Blocks[i];
                if (!IsJumpOnlyBlock(block, out var successor)) continue;

                // A block that only jumps to itself is a degenerate infinite
                // loop; leave it alone.
                if (ReferenceEquals(successor, block)) continue;

                // Redirect every explicit reference to this block (in any
                // block's instructions) to its single successor.
                RedirectAllReferences(function, block, successor);

                // Physically remove the block only if nothing falls through into
                // it (the preceding block ends in a terminator). Otherwise the
                // explicit predecessors are still redirected, but we keep the
                // block so the fall-through edge stays correct.
                if (CanPhysicallyRemove(function, i))
                {
                    function.Blocks.RemoveAt(i);
                    changed = true;
                    break; // indices shifted; restart the scan
                }
            }
        } while (changed);

        function.RebuildCfg();
    }

    // True if `block`'s only code-generating instruction is an unconditional
    // `mos6502.jmp.abs` naming a single successor block. Identity `pseudo.copy
    // $r -> $r` instructions are treated as transparent: they generate no code
    // and CopyElimination (which runs right after this pass) will delete them.
    // This mirrors llvm-mos's IsEmptyBlock, which skips instructions that don't
    // generate code (debug values).
    private static bool IsJumpOnlyBlock(MirBlock block, out MirBlock successor)
    {
        successor = null!;

        MirInstruction? terminator = null;
        foreach (var instr in block.Instructions)
        {
            if (IsIdentityCopy(instr)) continue;
            if (terminator is not null) return false; // more than one real instr
            terminator = instr;
        }

        if (terminator is null) return false;
        if (terminator.Opcode.Dialect != MOS6502Dialect.Id) return false;
        if ((MOS6502Op)terminator.Opcode.Code != MOS6502Op.JmpAbs) return false;
        if (terminator.Operands.Length != 1) return false;
        if (terminator.Operands[0] is not BlockTarget target) return false;

        successor = target.Block;
        return true;
    }

    // An identity copy `$r = pseudo.copy $r` — generates no code and is dropped
    // by CopyElimination. Treated as transparent when deciding emptiness.
    private static bool IsIdentityCopy(MirInstruction instr)
    {
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        return instr.Operands.Length == 2
            && instr.Operands[0] is PhysicalReg { IsDefinition: true } def
            && instr.Operands[1] is PhysicalReg { IsDefinition: false } use
            && def.Id == use.Id;
    }

    // Rewrite every BlockTarget operand across the whole function that points at
    // `from` to point at `to` instead.
    private static void RedirectAllReferences(MirFunction function, MirBlock from, MirBlock to)
    {
        foreach (var block in function.Blocks)
        {
            if (ReferenceEquals(block, from)) continue;
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var k = 0; k < operands.Length; k++)
                {
                    if (operands[k] is BlockTarget target && ReferenceEquals(target.Block, from))
                        operands[k] = target with { Block = to };
                }
            }
        }
    }

    // A block at index `i` may be physically removed only when nothing falls
    // through into it from the physically-preceding block — i.e. that block's
    // last instruction is a terminator (jmp / rts), so control never reaches
    // block `i` by fall-through.
    private static bool CanPhysicallyRemove(MirFunction function, int i)
    {
        var prev = function.Blocks[i - 1];
        if (prev.Instructions.Count == 0) return false;

        var last = prev.Instructions[^1];
        if (last.Opcode.Dialect != MOS6502Dialect.Id) return false;
        return DialectRegistry.ById(MOS6502Dialect.Id).IsTerminator(last.Opcode.Code);
    }
}
