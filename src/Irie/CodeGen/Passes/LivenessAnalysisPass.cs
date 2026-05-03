namespace Irie.CodeGen.Passes;

// Computes liveness information for a MachineFunction.
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
// Only virtual registers are tracked in LiveIn/LiveOut/RangeOf.
// Physical register interference (from MachineBasicBlock.LiveIns and
// MOS6502InstructionInfo.ImplicitDefs) is left for the allocator to query
// directly rather than being pre-computed here.
public sealed class LivenessAnalysisPass : MachineFunctionPass
{
    public override string Name => "LivenessAnalysis";

    // Run as a transform pass (no-op; just computes and discards).
    public override void Run(MachineFunction function) => Compute(function);

    public Liveness Compute(MachineFunction function)
    {
        function.RebuildCfg();

        var blocks = function.Blocks;
        var rpo = ComputeReversePostOrder(function);

        // Per-block upward-exposed variables and killed variables.
        // Process uses before defs within each instruction (handles the case
        // where a vreg is both used and re-defined in the same instruction).
        var upwardExposed = new Dictionary<MachineBasicBlock, HashSet<int>>(blocks.Count);
        var killedVariables = new Dictionary<MachineBasicBlock, HashSet<int>>(blocks.Count);

        foreach (var block in blocks)
        {
            var exposed = new HashSet<int>();
            var killed = new HashSet<int>();
            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                    if (op is VirtualRegisterOperand { IsDefinition: false } v && !killed.Contains(v.VirtualRegister))
                        exposed.Add(v.VirtualRegister);
                foreach (var op in instr.Operands)
                    if (op is VirtualRegisterOperand { IsDefinition: true } v)
                        killed.Add(v.VirtualRegister);
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
        var slotOf = new Dictionary<MachineInstruction, int>();
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

            // Live-in vregs are live from the block's first slot.
            foreach (var vreg in liveIn[block])
                Extend(vreg, firstSlot);

            // Each instruction's def/use participates in the range.
            foreach (var instr in block.Instructions)
            {
                var s = slotOf[instr];
                foreach (var op in instr.Operands)
                    if (op is VirtualRegisterOperand v)
                        Extend(v.VirtualRegister, s);
            }

            // Live-out vregs are live through the block's last slot.
            if (lastSlot >= firstSlot)
                foreach (var vreg in liveOut[block])
                    Extend(vreg, lastSlot);
        }

        var rangeOf = ranges.ToDictionary(
            kv => kv.Key,
            kv => new LiveRange(kv.Value.start, kv.Value.end));

        return new Liveness(slotOf, rangeOf, liveIn, liveOut);
    }

    // DFS post-order reversed = reverse post-order.
    // Unreachable blocks are appended in function-list order.
    private static List<MachineBasicBlock> ComputeReversePostOrder(MachineFunction function)
    {
        if (function.Blocks.Count == 0) return [];

        var visited = new HashSet<MachineBasicBlock>();
        var postOrder = new List<MachineBasicBlock>(function.Blocks.Count);

        void Dfs(MachineBasicBlock block)
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
