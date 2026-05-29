using Irie.CodeGen;
using Irie.Mir;

namespace Irie.Target;

// Top-of-tree target abstraction for the unified MIR pipeline. Targets
// register their dialect with DialectRegistry from their constructor and
// expose the lowering hooks the generic passes need.
//
// Step 7 surfaces Dialect + CallLowering + LegalizerInfo + GetRegisterName.
// Later steps (per notes/unified-ir-plan.md §6) will add PseudoExpander and
// the AddPostRegisterAllocationPasses hook.
public abstract class Target
{
    public abstract Dialect Dialect { get; }
    public abstract CallLowering CallLowering { get; }
    public abstract LegalizerInfo LegalizerInfo { get; }
    public abstract InstructionSelector InstructionSelector { get; }

    // Allocatable-register / class metadata for the unified-MIR register
    // allocator. The same TargetRegisterInfo type used by the legacy CodeGen
    // pipeline (per unified-IR plan §9: "TargetRegisterInfo.cs → kept under
    // Target/").
    public abstract TargetRegisterInfo RegisterInfo { get; }

    // Display name for a physical-register ID. Used by MirWriter to print
    // `$A` instead of `$0`, etc.
    public abstract string GetRegisterName(int physReg);
}
