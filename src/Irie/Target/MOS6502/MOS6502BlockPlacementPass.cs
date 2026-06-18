using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private block-placement — the llvm-mos `block-placement`
// (MachineBlockPlacement) analogue, in its minimal fall-through form. Runs LAST
// among the MOS6502 passes (immediately after MOS6502LateOptimizationPass),
// mirroring llvm-mos which runs block-placement after mos-late-opt.
//
// The goal is to order blocks so that a block's unconditional `mos6502.jmp.abs`
// target is laid out immediately after it, then drop the now-redundant jmp and
// let control fall through. This removes the explicit JMP on every straight-line
// edge.
//
// Correctness — fall-through semantics:
//   * Conditional branches (bne/beq/bmi/bpl/…) are NOT terminators in our model:
//     on the not-taken path control falls through to the *next physically-laid-
//     out block*. A block ending in a conditional branch (i.e. whose last
//     instruction is not a terminator) therefore has a *required* physical
//     successor — the block that currently follows it — which MUST stay
//     immediately after it. We model this as an unbreakable "forced" edge: the
//     forced successor is never pulled elsewhere (it is excluded from jmp-target
//     chaining and never used as an independent chain seed), so it is always
//     still available to be appended right after its forced predecessor.
//   * A jmp is dropped only when its target ends up immediately next, so any
//     conditional branch sitting before that jmp keeps the exact same
//     fall-through block it had before.
//
// Algorithm (greedy chain building, MachineBlockPlacement's core idea without
// the profile-guided heuristics): start at the entry block; repeatedly place a
// block, then extend the chain to (a) its forced fall-through successor if it
// has one, else (b) its jmp target if that target is still unplaced and has no
// forced predecessor. When the chain can't be extended, resume from the next
// unplaced, non-forced-successor block in original order. Entry block is always
// first.
public sealed class MOS6502BlockPlacementPass : MirFunctionPass
{
    public override string Name => "MOS6502BlockPlacement";

    public override void Run(MirFunction function)
    {
        if (function.Blocks.Count <= 1) return;

        var order = BuildOrder(function);

        // Commit the new physical order.
        function.Blocks.Clear();
        function.Blocks.AddRange(order);

        // Drop every `jmp.abs T` whose target is now the immediately-following
        // block (fall-through).
        for (var i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            if (block.Instructions.Count == 0) continue;

            var last = block.Instructions[^1];
            if (!IsUnconditionalJmp(last, out var target)) continue;

            var next = i + 1 < function.Blocks.Count ? function.Blocks[i + 1] : null;
            if (ReferenceEquals(target, next))
                block.Instructions.RemoveAt(block.Instructions.Count - 1);
        }

        function.RebuildCfg();
    }

    private static List<MirBlock> BuildOrder(MirFunction function)
    {
        var original = function.Blocks;
        var placed = new HashSet<MirBlock>();
        var order = new List<MirBlock>(original.Count);

        // Map each block to the block that originally followed it (its
        // fall-through neighbour).
        var originalNext = new Dictionary<MirBlock, MirBlock>();
        for (var i = 0; i + 1 < original.Count; i++)
            originalNext[original[i]] = original[i + 1];

        // A block has a "forced predecessor" when the block physically before it
        // ends in a non-terminator (a conditional branch / fall-through), so
        // control reaches it by fall-through and it MUST stay immediately after
        // that predecessor. Such blocks may never be pulled elsewhere.
        var hasForcedPredecessor = new HashSet<MirBlock>();
        for (var i = 0; i + 1 < original.Count; i++)
        {
            var block = original[i];
            if (block.Instructions.Count == 0 || !IsTerminator(block.Instructions[^1]))
                hasForcedPredecessor.Add(original[i + 1]);
        }

        // Seed the chains with the entry block first, then the rest in original
        // order. Forced-successor blocks are never seeded — they are placed only
        // as the fall-through continuation of their forced predecessor's chain.
        var seeds = new Queue<MirBlock>(original);

        while (order.Count < original.Count)
        {
            MirBlock? seed = null;
            while (seeds.Count > 0)
            {
                var candidate = seeds.Dequeue();
                if (placed.Contains(candidate)) continue;
                if (hasForcedPredecessor.Contains(candidate)) continue;
                seed = candidate;
                break;
            }

            // Fallback: if every remaining unplaced block has a forced
            // predecessor (can only happen with malformed CFGs), place them in
            // original order to guarantee termination.
            if (seed is null)
            {
                foreach (var block in original)
                    if (!placed.Contains(block)) { seed = block; break; }
                if (seed is null) break;
            }

            var current = seed;
            while (current is not null && !placed.Contains(current))
            {
                order.Add(current);
                placed.Add(current);
                current = NextInChain(current, originalNext, placed, hasForcedPredecessor);
            }
        }

        return order;
    }

    // Choose the block to place immediately after `block`:
    //   1. If `block`'s last instruction is not a terminator, control falls
    //      through to its original neighbour — that neighbour MUST come next.
    //   2. Otherwise, if `block` ends in `jmp.abs T`, T is unplaced and T has no
    //      forced predecessor, prefer T so the jmp can fall through.
    private static MirBlock? NextInChain(
        MirBlock block,
        Dictionary<MirBlock, MirBlock> originalNext,
        HashSet<MirBlock> placed,
        HashSet<MirBlock> hasForcedPredecessor)
    {
        if (block.Instructions.Count == 0 || !IsTerminator(block.Instructions[^1]))
        {
            // Forced fall-through to the original neighbour. It carries a forced
            // predecessor (this block), so it was never seeded or pulled
            // elsewhere and is still unplaced here.
            return originalNext.TryGetValue(block, out var fall) && !placed.Contains(fall)
                ? fall
                : null;
        }

        if (IsUnconditionalJmp(block.Instructions[^1], out var target)
            && !placed.Contains(target)
            && !hasForcedPredecessor.Contains(target))
        {
            return target;
        }

        return null;
    }

    private static bool IsTerminator(MirInstruction instr)
        => instr.Opcode.Dialect == MOS6502Dialect.Id
           && DialectRegistry.ById(MOS6502Dialect.Id).IsTerminator(instr.Opcode.Code);

    private static bool IsUnconditionalJmp(MirInstruction instr, out MirBlock target)
    {
        target = null!;
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;
        if ((MOS6502Op)instr.Opcode.Code != MOS6502Op.JmpAbs) return false;
        if (instr.Operands.Length != 1) return false;
        if (instr.Operands[0] is not BlockTarget bt) return false;
        target = bt.Block;
        return true;
    }
}
