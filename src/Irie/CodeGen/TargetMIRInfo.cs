namespace Irie.CodeGen;

public abstract class TargetMIRInfo
{
    // Writer-side: int → name
    public abstract string? GetOpcodeName(int opcode);
    public abstract string  GetRegisterName(int register);
    public abstract string? GetRegisterClassName(int classId);
    public abstract int[]?  GetTiedOperands(int opcode);

    // Parser-side: name → int
    public abstract int? ParseOpcode(string name);
    public abstract int? ParseRegister(string name);
    public abstract int? ParseRegisterClass(string name);
}
