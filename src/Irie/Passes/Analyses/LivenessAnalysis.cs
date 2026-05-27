using Irie.Mir;

namespace Irie.Passes.Analyses;

// Computes liveness information for a MirFunction.
//
// Algorithm: standard backward dataflow (Appel / Cooper-Harvey-Kennedy).
//   LiveOut[B] = ⋃ LiveIn[S]  for each successor S
//   LiveIn[B]  = UEVar[B] ∪ (LiveOut[B] − VarKill[B])
//
// Iterates in reverse post-order until a fixed point is reached.
// After dataflow, a forward slot-numbering pass widens per-vreg LiveRange
// entries by extending them to cover every slot in a block where the vreg
// is live-in, defined, used, or live-out.
//
// Block parameters count as defs at block entry (killed before any instruction
// runs). BlockTarget.Args are uses in the terminator's block.
//
// Only virtual registers are tracked in LiveIn/LiveOut/RangeOf.
// Physical register interference (from MirBlock.LiveIns and any per-dialect
// implicit-def metadata) is left for the allocator to query directly rather
// than being pre-computed here.
public sealed class LivenessAnalysis : MirFunctionAnalysis<Liveness>
{
    public override Liveness Compute(MirFunction function)
    {
        function.RebuildCfg();

        var blocks = function.Blocks;
        var rpo = ComputeReversePostOrder(function);

        // Per-block upward-exposed variables and killed variables.
        // Process uses before defs within each instruction (handles the case
        // where a vreg is both used and re-defined in the same instruction).
        var upwardExposed = new Dictionary<MirBlock, HashSet<int>>(blocks.Count);
        var killedVariables = new Dictionary<MirBlock, HashSet<int>>(blocks.Count);

        foreach (var block in blocks)
        {
            var exposed = new HashSet<int>();
            var killed = new HashSet<int>();

            // Block parameters are defined at block entry.
            foreach (var param in block.Parameters)
                killed.Add(param);

            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                    foreach (var vreg in EnumerateVRegUses(op))
                        if (!killed.Contains(vreg))
                            exposed.Add(vreg);
                foreach (var op in instr.Operands)
                    if (op is VirtualReg { IsDefinition: true } v)
                        killed.Add(v.Id);
            }
            upwardExposed[block] = exposed;
            killedVariables[block] = killed;
        }

        var liveIn = blocks.ToDictionary(b => b, _ => new HashSet<int>());
        var liveOut = blocks.ToDictionary(b => b, _ => new HashSet<int>());

        // Backward dataflow: iterate until fixed point.
        bool changed;
        do
        {
            changed = false;
            foreach (var block in rpo)
            {
                var newLiveOut = new HashSet<int>();
                foreach (var succ in block.Successors)
                    newLiveOut.UnionWith(liveIn[succ]);

                var newLiveIn = new HashSet<int>(upwardExposed[block]);
                foreach (var v in newLiveOut)
                    if (!killedVariables[block].Contains(v))
                        newLiveIn.Add(v);

                if (!newLiveOut.SetEquals(liveOut[block]) || !newLiveIn.SetEquals(liveIn[block]))
                {
                    liveOut[block] = newLiveOut;
                    liveIn[block] = newLiveIn;
                    changed = true;
                }
            }
        }
        while (changed);

        // Forward pass: number each instruction with a slot index, then
        // widen per-vreg ranges to cover every slot where the vreg is live.
        var slotOf = new Dictionary<MirInstruction, int>();
        var ranges = new Dictionary<int, (int start, int end)>();

        void Extend(int vreg, int slot)
        {
            if (ranges.TryGetValue(vreg, out var r))
                ranges[vreg] = (Math.Min(r.start, slot), Math.Max(r.end, slot));
            else
                ranges[vreg] = (slot, slot);
        }

        var slot = 0;
        foreach (var block in blocks)
        {
            var firstSlot = slot;
            foreach (var instr in block.Instructions)
                slotOf[instr] = slot++;

            var lastSlot = slot - 1;

            // Block parameters are live from the block's first slot.
            foreach (var vreg in block.Parameters)
                Extend(vreg, firstSlot);

            // Live-in vregs are live from the block's first slot.
            foreach (var vreg in liveIn[block])
                Extend(vreg, firstSlot);

            // Each instruction's def/use participates in the range.
            foreach (var instr in block.Instructions)
            {
                var s = slotOf[instr];
                foreach (var op in instr.Operands)
                {
                    switch (op)
                    {
                        case VirtualReg v:
                            Extend(v.Id, s);
                            break;
                        case BlockTarget bt:
                            foreach (var vreg in EnumerateVRegUsesInArgs(bt.Args))
                                Extend(vreg, s);
                            break;
                    }
                }
            }

            // Live-out vregs are live through the block's last slot.
            if (lastSlot >= firstSlot)
                foreach (var vreg in liveOut[block])
                    Extend(vreg, lastSlot);
        }

        return new Liveness(
             slotOf,
             ranges.ToDictionary(kv => kv.Key, kv => new LiveRange(kv.Value.start, kv.Value.end)),
             liveIn,
             liveOut);
    }

    // Vreg uses, recursing into BlockTarget.Args.
    private static IEnumerable<int> EnumerateVRegUses(MirOperand operand)
    {
        switch (operand)
        {
            case VirtualReg { IsDefinition: false } v:
                yield return v.Id;
                break;
            case BlockTarget bt:
                foreach (var vreg in EnumerateVRegUsesInArgs(bt.Args))
                    yield return vreg;
                break;
        }
    }

    private static IEnumerable<int> EnumerateVRegUsesInArgs(MirOperand[] args)
    {
        foreach (var arg in args)
            foreach (var vreg in EnumerateVRegUses(arg))
                yield return vreg;
    }

    // DFS post-order reversed = reverse post-order.
    // Unreachable blocks are appended in function-list order.
    private static List<MirBlock> ComputeReversePostOrder(MirFunction function)
    {
        if (function.Blocks.Count == 0) return [];

        var visited = new HashSet<MirBlock>();
        var postOrder = new List<MirBlock>(function.Blocks.Count);

        void Dfs(MirBlock block)
        {
            if (!visited.Add(block)) return;
            foreach (var succ in block.Successors)
                Dfs(succ);
            postOrder.Add(block);
        }

        Dfs(function.Blocks[0]);

        foreach (var block in function.Blocks)
            if (!visited.Contains(block))
                postOrder.Add(block);

        postOrder.Reverse();
        return postOrder;
    }
}
