using Irie.Mir;

namespace Irie.Target;

// MIR-shaped instruction selector. The InstructionSelectorPass walks each
// function's instructions in order, asking the target's selector to lower
// each one to target-specific opcodes. Mirrors the old
// Irie.CodeGen.InstructionSelector, but talks in MirInstruction / MirBuilder.
public abstract class InstructionSelector
{
    // Called once per function so the selector can reset any per-function
    // state (e.g. merge maps) before processing begins.
    public virtual void BeginFunction(MirFunction function) { }

    // Select a single instruction. The implementation may emit replacement
    // instructions via the builder and call builder.Remove(instruction)
    // for the original. Returns true if the instruction was handled
    // (including pass-through cases that need no change); false if
    // the selector has no rule for this opcode.
    public abstract bool Select(MirInstruction instruction, MirBuilder builder);
}
