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

    // Physical registers a `pseudo.copy` destroys as hidden scratch when the
    // target lowers it, beyond the copy's own dst def and src use. On the
    // MOS6502 a copy that materialises an immediate into — or moves between —
    // zero-page slots expands to `LDA;STA`, clobbering $A even though $A is
    // neither the copy's source nor destination. The register allocator records
    // a clobber of each returned physreg at the copy's slot so it won't keep an
    // unrelated live value there across the copy.
    //
    // Called during clobber analysis, *before* vregs are assigned to physregs,
    // so the only information available is the copy's source operand and the
    // (already-resolved) source and destination register classes. `srcClassId`
    // is 0 (None) when the source is an Immediate. A returned physreg must be a
    // clobber that is *certain* given only this much information: implementations
    // should report the forms that unconditionally route through scratch (an
    // immediate has no register choice and the copy can never be coalesced away)
    // and leave the may-coalesce register/zp moves to the allocator's physreg
    // reservation model rather than over-approximating scarce scratch out of
    // reach. Returns an empty span by default (no hidden-scratch copies).
    public virtual ReadOnlySpan<int> GetPseudoCopyScratchClobbers(
        MirOperand source, int srcClassId, int destClassId) => default;
}
