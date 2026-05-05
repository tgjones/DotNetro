using Irie.CodeGen;

namespace Irie.Target.MOS6502;

public sealed class MOS6502MIRInfo : TargetMIRInfo
{
    public override string? GetOpcodeName(int opcode)      => MOS6502InstructionInfo.GetDisplayName(opcode);
    public override string  GetRegisterName(int register)  => MOS6502Registers.NameOf(register);
    public override string? GetRegisterClassName(int id)   => MOS6502RegisterClass.GetName(id);
    public override int[]?  GetTiedOperands(int opcode)    => MOS6502InstructionInfo.TryGet(opcode)?.TiedOperands;
    public override int?    ParseOpcode(string name)       => MOS6502InstructionInfo.ParseDisplayName(name);
    public override int?    ParseRegister(string name)     => MOS6502Registers.TryParse(name);
    public override int?    ParseRegisterClass(string name) =>
        MOS6502RegisterClass.TryParse(name, out var id) ? id : null;
}
