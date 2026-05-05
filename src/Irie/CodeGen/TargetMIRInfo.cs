namespace Irie.CodeGen;

public abstract class TargetMIRInfo
{
    // Writer-side: int → name
    public abstract string  GetRegisterName(int register);
    public abstract string? GetRegisterClassName(int classId);

    // Parser-side: name → int
    public abstract int? ParseRegister(string name);
    public abstract int? ParseRegisterClass(string name);
}
