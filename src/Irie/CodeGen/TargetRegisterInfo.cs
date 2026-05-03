namespace Irie.CodeGen;

public abstract class TargetRegisterInfo
{
    // Allocatable physical registers for the given class, in preferred-allocation order.
    public abstract ReadOnlySpan<int> GetAllocatableRegisters(int classId);

    // True if physReg belongs to (or aliases) the given class.
    public abstract bool BelongsToClass(int physReg, int classId);
}
