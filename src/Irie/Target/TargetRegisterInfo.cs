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

    // The scratch locations acceptable for a SPECIFIC scratch-form copy, in
    // preference order. Some copy shapes admit more than a GPR — e.g. on the
    // MOS6502 an $x↔$y copy can bounce through a dead zero-page slot
    // (`STX $tmp ; LDY $tmp`) as well as through $a — so the candidate set is
    // shape-dependent. The scavenger picks the first candidate DEAD at the copy's
    // point. Defaults to GetScratchGprCandidates() (shape-independent GPR scratch).
    public virtual ReadOnlySpan<int> GetScratchCandidates(MirInstruction scratchCopy)
        => GetScratchGprCandidates();

    // Locations the scavenger may use to *emergency-save* a live scratch register
    // when no scratch candidate is free across a copy's point: it picks one of
    // these that is itself dead there, saves the chosen GPR into it, performs the
    // copy, and restores the GPR (plan §3.4/§3.6 minimal save/restore). On the
    // MOS6502 these are the spare zero-page slots — a memory store/load needs no
    // further scratch. Empty by default (no emergency path available).
    public virtual ReadOnlySpan<int> GetEmergencyScratchSaveSlots() => default;

    // The scarce architectural general-purpose registers, in the order to PREFER
    // them for SHORT-lived flexible values (register-allocator-redesign Phase 5;
    // plan §3.5). These are the cheap real registers ($a/$x/$y on the MOS6502)
    // that the abundant memory/zero-page pool surrounds. The allocator's
    // cost-driven colour selection promotes these ahead of the memory pool for a
    // value whose live range is short (does not span the arithmetic chain), and
    // keeps the memory pool first for long-lived values so the GPRs stay free for
    // arithmetic. Empty by default (a target with no such distinction, where the
    // class's default allocatable order is used unchanged).
    //
    // This is preference POLICY only — it never changes which registers are
    // *legal* for a vreg (that is the class intersection). It only reorders the
    // legal set so allocation converges toward the llvm-mos references, which use
    // $a/$x/$y for data where Irie used to park everything in zero page.
    public virtual ReadOnlySpan<int> GetShortRangeGprPreference() => default;

    // The callee-saved registers per the calling convention — registers a
    // clobbering callee must save in its prologue and restore in its epilogue.
    // Mirrors LLVM getCalleeSavedRegs. Empty by default.
    public virtual ReadOnlySpan<int> GetCalleeSavedRegisters() => default;
}
