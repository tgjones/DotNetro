namespace Irie.CodeGen;

public abstract class TargetInstructionInfo
{
    public abstract TargetInstructionDescription? TryGet(int opcode);
    public abstract TargetInstructionDescription  Get(int opcode);
    public abstract string?                       GetDisplayName(int opcode);
    public abstract int?                          ParseDisplayName(string name);
}
