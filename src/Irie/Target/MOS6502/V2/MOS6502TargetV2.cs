using Irie.Mir;

namespace Irie.Target.MOS6502.V2;

// Parallel MOS6502 target for the unified-MIR pipeline (`iriec --engine=v2`).
// Lives alongside the existing Irie.Target.MOS6502.MOS6502Target until the
// stepwise rollout finishes (see notes/unified-ir-plan.md §10 step 17).
public sealed class MOS6502TargetV2 : Irie.Target.Target
{
    public MOS6502TargetV2()
    {
        MirBootstrap.EnsureRegistered();
        if (!DialectRegistry.TryByPrefix("mos6502", out _))
            DialectRegistry.Register(new MOS6502Dialect());
    }

    public override Dialect Dialect => DialectRegistry.ByPrefix("mos6502");
    public override Irie.Target.CallLowering CallLowering { get; } = new MOS6502CallLowering();

    public override string GetRegisterName(int physReg) => MOS6502Registers.NameOf(physReg);
}
