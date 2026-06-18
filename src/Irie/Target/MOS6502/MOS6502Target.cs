using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// MOS6502 target for the unified-MIR pipeline driven by `iriec`.
public class MOS6502Target : Irie.Target.Target
{
    public MOS6502Target()
    {
        MirBootstrap.EnsureRegistered();
        DialectRegistry.GetOrRegister("mos6502", () => new MOS6502Dialect());
    }

    public override Dialect Dialect => DialectRegistry.ByPrefix("mos6502");
    public override Irie.Target.CallLowering CallLowering { get; } = new MOS6502CallLowering();
    public override Irie.Target.LegalizerInfo LegalizerInfo { get; } = new MOS6502LegalizerInfo();
    public override Irie.Target.InstructionSelector InstructionSelector { get; } = new MOS6502InstructionSelector();
    public override Irie.Target.PseudoExpander PseudoExpander { get; } = new MOS6502PseudoExpander();
    public override Irie.Target.BranchLowering BranchLowering { get; } = new MOS6502BranchLowering();
    public override Irie.Target.FrameLowering FrameLowering { get; } = new MOS6502FrameLowering();
    public override TargetRegisterInfo RegisterInfo { get; } = new MOS6502RegisterInfo();
    public override Irie.Target.MachineCodeEmitter MachineCodeEmitter { get; } = new MOS6502MachineCodeEmitter();

    public override string GetRegisterName(int physReg) => MOS6502Registers.NameOf(physReg);

    public override void AddPreInstructionSelectionPasses(Irie.Passes.PassManager pm)
    {
        // Expand materialized arith.select into cf.cond_br diamonds (the llvm-mos
        // MOSLowerSelect analogue) on legalized, still-SSA MIR.
        pm.AddPass(new MOS6502SelectLoweringPass());
    }

    public override void AddBranchFoldingPasses(Irie.Passes.PassManager pm)
    {
        // The llvm-mos branch-folder analogue: fold away a block whose only
        // instruction is an unconditional jmp, redirecting its predecessors to
        // the successor (e.g. the empty bb1 that only does `JMP .bb3`). Runs
        // after RA and before CopyElimination, matching llvm-mos's Control Flow
        // Optimizer position (after the VReg rewriter, before copy-opt).
        pm.AddPass(new MOS6502BranchFolderPass());
    }

    public override void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm)
    {
        pm.AddPass(new MOS6502AddressingModeSelectorPass());
        pm.AddPass(new MOS6502IncrementStrengthReductionPass());
        pm.AddPass(new MOS6502ParallelCopyPass());
        // No frame-placement pass yet: Stage 2 places every slot in absolute
        // memory (StackId == default) and the generic FrameAccessLoweringPass
        // (added by the driver right after these passes) expands each abstract
        // frame access to the indirect-Y sequence. Stage 3 adds a target-private
        // post-RA placement pass here that promotes eligible slots to zero page.
    }

    public override void AddPostPseudoExpansionPasses(Irie.Passes.PassManager pm)
    {
        // The mos-late-opt analogue: drop a `cmp $r, #0` that is redundant
        // because $r was just produced by a flag-setting load/transfer. Runs
        // after PseudoExpansion so it sees the final expanded loads/transfers
        // (matches llvm-mos, which runs mos-late-opt after post-RA pseudo
        // expansion).
        pm.AddPass(new MOS6502LateOptimizationPass());
    }

    public override void AddFinalPasses(Irie.Passes.PassManager pm)
    {
        // The llvm-mos block-placement analogue (minimal fall-through form):
        // order blocks so a block's unconditional jmp target follows it, then
        // drop the redundant jmp and fall through. Mirrors llvm-mos, which runs
        // block-placement as the last codegen pass.
        //
        // It runs AFTER RegisterScavenging (not immediately after the late-opt
        // pass) because dropping a fall-through jmp turns an explicit CFG edge
        // implicit: RegisterScavenging derives physreg liveness from the
        // explicit-terminator CFG (MirBlock.Successors / LiveIns), so it must
        // observe the jmp before block-placement removes it. With block layout as
        // the final step, the MachineCodeEmitter (which knows fall-through) is the
        // only consumer of the post-placement CFG.
        pm.AddPass(new MOS6502BlockPlacementPass());
    }

    // Hand-written MIR runtime (currently just the indirect-call trampoline;
    // OS-call wrappers, `start`, ManagedHeap_Alloc, etc. will be added in later
    // plan steps). Loaded as an embedded resource; the IL→MIR translator will
    // parse this string and merge the functions into the produced module.
    public virtual string GetRuntime()
    {
        const string resourceName = "Irie.Target.MOS6502.runtime.irie";
        using var stream = typeof(MOS6502Target).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded runtime resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
