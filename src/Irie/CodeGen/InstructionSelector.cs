namespace Irie.CodeGen;

public abstract class InstructionSelector
{
    // Called once before processing each function, allowing the selector to
    // reset any per-function state (e.g. merge maps).
    public virtual void BeginFunction(MachineFunction function) { }

    // Select a single instruction, replacing it with target-specific instructions
    // using the builder. Returns true if the instruction was handled (including
    // passthrough instructions that need no change), false if unsupported.
    // Handled instructions that should be removed must call builder.Remove(instr).
    public abstract bool Select(MachineInstruction instruction, MachineIRBuilder builder);
}
