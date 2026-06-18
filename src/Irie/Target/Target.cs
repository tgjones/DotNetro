using Irie.Mir;

namespace Irie.Target;

// Top-of-tree target abstraction for the unified MIR pipeline. Targets
// register their dialect with DialectRegistry from their constructor and
// expose the lowering hooks the generic passes need.
public abstract class Target
{
    public abstract Dialect Dialect { get; }
    public abstract CallLowering CallLowering { get; }
    public abstract LegalizerInfo LegalizerInfo { get; }
    public abstract InstructionSelector InstructionSelector { get; }
    public abstract PseudoExpander PseudoExpander { get; }
    public abstract BranchLowering BranchLowering { get; }
    public abstract FrameLowering FrameLowering { get; }

    // Emits a MachineCodeModule from a post-PseudoExpansion MirModule. Called
    // by the driver after passMgr.Run(); not part of the pass pipeline.
    public abstract MachineCodeEmitter MachineCodeEmitter { get; }

    // Allocatable-register / class metadata for the unified-MIR register
    // allocator. The same TargetRegisterInfo type used by the legacy CodeGen
    // pipeline (per unified-IR plan §9: "TargetRegisterInfo.cs → kept under
    // Target/").
    public abstract TargetRegisterInfo RegisterInfo { get; }

    // Display name for a physical-register ID. Used by MirWriter to print
    // `$A` instead of `$0`, etc.
    public abstract string GetRegisterName(int physReg);

    // Called by the driver between LegalizerPass and InstructionSelectorPass.
    // Targets append passes that run on legalized, still-SSA MIR before isel —
    // e.g. MOS6502's select-lowering, which expands materialized arith.select into
    // a cf.cond_br diamond (the llvm-mos MOSLowerSelect analogue, which likewise
    // runs after the legalizer and before instruction selection).
    public virtual void AddPreInstructionSelectionPasses(Irie.Passes.PassManager pm) { }

    // Called by the driver between RegisterAllocatorPass and CopyEliminationPass.
    // Targets append their branch-folder passes here — the llvm-mos Control Flow
    // Optimizer slot, which runs after the VReg rewriter and before copy-opt
    // (Irie's CopyElimination). MOS6502 uses this for empty-block elimination.
    public virtual void AddBranchFoldingPasses(Irie.Passes.PassManager pm) { }

    // Called by iriec between CopyEliminationPass and PseudoExpansionPass.
    // Targets append their own post-RA passes (e.g. addressing-mode selection
    // for MOS6502) — there is no generic stage at this point in the pipeline.
    public virtual void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm) { }

    // Called by iriec after PseudoExpansionPass (and before RegisterScavenging).
    // Targets append late-optimization passes that need the final expanded
    // physreg-only stream — e.g. MOS6502's flag-from-load fold, the llvm-mos
    // `mos-late-opt` analogue, which likewise runs after post-RA pseudo
    // expansion.
    public virtual void AddPostPseudoExpansionPasses(Irie.Passes.PassManager pm) { }

    // Called by the driver as the very last stage, AFTER RegisterScavenging.
    // Targets append final block-layout passes here — the llvm-mos
    // block-placement slot (the last codegen pass before emission). It must run
    // after scavenging because block-placement drops the explicit unconditional
    // jmp on a fall-through edge, turning that edge implicit; RegisterScavenging
    // (which derives physreg liveness from the explicit-terminator CFG via
    // MirBlock.LiveIns / Successors) must therefore have already run.
    public virtual void AddFinalPasses(Irie.Passes.PassManager pm) { }

    // Default origin (load address) when --origin is not supplied; null = no opinion.
    public virtual int? DefaultOrigin => null;

    // Packages a flat 6502 byte buffer into a target-system image format; default is identity.
    public virtual byte[] PackageImage(byte[] code, int origin) => code;
}
