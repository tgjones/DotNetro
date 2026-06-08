using Irie.Mir;

namespace Irie.Target;

public abstract class TargetRegisterInfo
{
    // Allocatable physical registers for the given class, in preferred-allocation order.
    public abstract ReadOnlySpan<int> GetAllocatableRegisters(int classId);

    // True if physReg belongs to (or aliases) the given class.
    public abstract bool BelongsToClass(int physReg, int classId);

    // The "natural" single-register class a physical register belongs to
    // (e.g. on the MOS6502 $A -> Ac, a zero-page slot -> Imag8). Returns 0
    // (no class) for registers that are not part of any allocatable class.
    public abstract int ClassOfPhysicalRegister(int physReg);

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

    // Scratch general-purpose registers the post-RA RegisterScavengingPass may
    // hand to a `pseudo.copy` that needs a temporary register to lower (e.g. on
    // the MOS6502 an immediate-into-zero-page copy needs a GPR for `LD? #imm ;
    // ST? $zp`). Returned in preference order; the scavenger picks the first one
    // that is DEAD at the copy's program point. Empty by default (a target whose
    // copies never need scratch).
    //
    // This REPLACES the old GetPseudoCopyScratchClobbers hook (deleted): copy
    // scratch is no longer modelled during register allocation at all. The
    // allocator allocates as if copies were free; the scavenger picks scratch
    // afterwards with exact post-RA liveness (plan §3.6, mirroring llvm-mos's
    // MOSPostRAScavenging). The benefit: scratch can be $x/$y when those are
    // dead, instead of forcing $a free for every copy.
    public virtual ReadOnlySpan<int> GetScratchGprCandidates() => default;
}
