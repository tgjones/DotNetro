namespace Irie.CodeGen;

public abstract class Target
{
    public abstract TargetInstructionInfo CreateInstructionInfo();
    public abstract TargetMIRInfo         CreateMIRInfo();
    public abstract CallLowering          CreateCallLowering();
    public abstract LegalizerInfo         CreateLegalizerInfo();
    public abstract InstructionSelector   CreateInstructionSelector();
    public abstract TargetRegisterInfo    CreateRegisterInfo();
}
