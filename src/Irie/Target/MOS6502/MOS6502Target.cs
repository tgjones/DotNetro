using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// MOS6502 target for the unified-MIR pipeline driven by `iriec`.
public sealed class MOS6502Target : Irie.Target.Target
{
    public MOS6502Target()
    {
        MirBootstrap.EnsureRegistered();
        if (!DialectRegistry.TryByPrefix("mos6502", out _))
            DialectRegistry.Register(new MOS6502Dialect());
    }

    public override Dialect Dialect => DialectRegistry.ByPrefix("mos6502");
    public override Irie.Target.CallLowering CallLowering { get; } = new MOS6502CallLowering();
    public override Irie.Target.LegalizerInfo LegalizerInfo { get; } = new MOS6502LegalizerInfo();
    public override Irie.Target.InstructionSelector InstructionSelector { get; } = new MOS6502InstructionSelector();
    public override Irie.Target.PseudoExpander PseudoExpander { get; } = new MOS6502PseudoExpander();
    public override TargetRegisterInfo RegisterInfo { get; } = new MOS6502RegisterInfo();
    public override Irie.Target.MachineCodeEmitter MachineCodeEmitter { get; } = new MOS6502MachineCodeEmitter();

    public override string GetRegisterName(int physReg) => MOS6502Registers.NameOf(physReg);

    public override void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm)
    {
        pm.AddPass(new MOS6502AddressingModeSelectorPass());
    }
}
