namespace Irie.Target;

public abstract class TargetRegisterInfo
{
    // Allocatable physical registers for the given class, in preferred-allocation order.
    public abstract ReadOnlySpan<int> GetAllocatableRegisters(int classId);

    // True if physReg belongs to (or aliases) the given class.
    public abstract bool BelongsToClass(int physReg, int classId);

    // Writer-side: int → name
    public abstract string  GetRegisterName(int register);
    public abstract string? GetRegisterClassName(int classId);

    // Parser-side: name → int
    public abstract int? ParseRegister(string name);
    public abstract int? ParseRegisterClass(string name);

    // ID of a "flexible 8-bit" register class — a class containing every 8-bit
    // location the target is willing to route a value through. Used by the
    // unified-MIR register allocator to widen live-in vregs into a class that
    // permits multiple physregs, so they can be spread out and routed back to
    // specific physregs (e.g. A) via constraint-fixup copies as needed.
    // Return 0 (no class) on targets where no widening is desired.
    public virtual int FlexibleI8ClassId => 0;
}
