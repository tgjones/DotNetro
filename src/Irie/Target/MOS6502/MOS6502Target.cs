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
