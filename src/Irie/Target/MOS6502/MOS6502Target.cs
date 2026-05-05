using Irie.CodeGen;

namespace Irie.Target.MOS6502;

public sealed class MOS6502Target : Irie.CodeGen.Target
{
    public override TargetMIRInfo        CreateMIRInfo()              => new MOS6502MIRInfo();
    public override CallLowering         CreateCallLowering()          => new MOS6502CallLowering();
    public override LegalizerInfo        CreateLegalizerInfo()         => new MOS6502LegalizerInfo();
    public override InstructionSelector  CreateInstructionSelector()   => new MOS6502InstructionSelector();
    public override TargetRegisterInfo   CreateRegisterInfo()          => new MOS6502RegisterInfo();
}
