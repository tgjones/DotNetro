using Irie.Mir;

namespace Irie.Target;

// Top-of-tree target abstraction for the unified MIR pipeline. Targets
// register their dialect with DialectRegistry from their constructor and
// expose the lowering hooks the generic passes need.
//
// Step 6 only surfaces Dialect + CallLowering + GetRegisterName. Later steps
// (per notes/unified-ir-plan.md §6) will add LegalizerInfo, InstructionSelector,
// PseudoExpander, and the AddPostRegisterAllocationPasses hook.
public abstract class Target
{
    public abstract Dialect Dialect { get; }
    public abstract CallLowering CallLowering { get; }

    // Display name for a physical-register ID. Used by MirWriter to print
    // `$A` instead of `$0`, etc.
    public abstract string GetRegisterName(int physReg);
}
