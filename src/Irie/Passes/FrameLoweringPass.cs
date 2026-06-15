using Irie.Dialects.Mem;
using Irie.Mir;

namespace Irie.Passes;

// Target-agnostic pass that materialises each function's FrameSlots as
// module-level MirGlobals and rewrites every `mem.frame_addr <i>` to a
// `mem.symbol @<slot_name>` (where the slot's symbol name was assigned
// at slot-creation time, conventionally `<func>_local<i>`).
//
// Runs before the legalizer so the rest of the pipeline only ever sees
// mem.symbol — plan §4.5 allows positioning anywhere between PhiElimination
// and ISel, but running early simplifies the pipeline by collapsing the
// frame-addr concept before legalization / instruction selection look at
// the IR.
public sealed class FrameLoweringPass : Pass
{
    public override string Name => "FrameLowering";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        var existingGlobals = new HashSet<string>();
        foreach (var global in module.Globals)
            existingGlobals.Add(global.SymbolName);

        foreach (var function in module.Functions)
        {
            // Register each FrameSlot as a zero-init MirGlobal (.bss-style)
            // if it isn't already present. This is idempotent so running
            // FrameLowering twice on the same module (e.g. across pass
            // restarts) doesn't duplicate globals.
            //
            // Every slot materialises as an absolute-memory global. Placement
            // into zero page (when a slot is promoted) is decided late, post-RA,
            // by the target's frame-access lowering — it is not a property of the
            // global; the slot carries its placement via StackId / Offset and the
            // late lowering reads it.
            foreach (var slot in function.FrameSlots)
            {
                if (!existingGlobals.Add(slot.SymbolName)) continue;
                module.Globals.Add(new MirGlobal(
                    SymbolName:  slot.SymbolName,
                    Type:        slot.Type,
                    SizeInBytes: slot.Type.SizeInBits / 8,
                    Initializer: null));
            }

            RewriteFrameAddrs(function);
        }
    }

    // Rewrites each `mem.frame_addr <slot_index>` in-place into the
    // equivalent `mem.symbol @<slot_symbol_name>`. The def operand is
    // preserved so downstream uses are untouched.
    private static void RewriteFrameAddrs(MirFunction function)
    {
        var slotsByIndex = new Dictionary<int, FrameSlot>();
        foreach (var slot in function.FrameSlots)
            slotsByIndex[slot.Index] = slot;

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr.Opcode.Dialect != MemDialect.Id) continue;
                if ((MemOp)instr.Opcode.Code != MemOp.FrameAddr) continue;

                if (instr.Operands.Length != 2
                    || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
                    || instr.Operands[1] is not Immediate indexImm)
                {
                    throw new InvalidOperationException(
                        "FrameLoweringPass: mem.frame_addr must have shape `%def : i16 = mem.frame_addr <slot_index>`.");
                }

                var slotIndex = (int)indexImm.Value;
                if (!slotsByIndex.TryGetValue(slotIndex, out var slot))
                {
                    throw new InvalidOperationException(
                        $"FrameLoweringPass: function @{function.Name} has no FrameSlot at index {slotIndex}.");
                }

                block.Instructions[i] = new MirInstruction(
                    MemDialect.OpRef(MemOp.Symbol),
                    [defReg, new Symbol(slot.SymbolName)])
                {
                    Parent = block,
                };
            }
        }
    }
}
