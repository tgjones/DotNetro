using Irie.Mir;

namespace Irie.Target.MOS6502;

public sealed class MOS6502RegisterInfo : TargetRegisterInfo
{
    private static readonly int[] AcRegs    = [MOS6502Registers.A];
    private static readonly int[] XcRegs    = [MOS6502Registers.X];
    private static readonly int[] YcRegs    = [MOS6502Registers.Y];
    private static readonly int[] CcRegs    = [MOS6502Registers.C];
    // RC0/RC1 reserved as soft stack pointer per CC_MOS; allocatable range is RC2..RC31.
    private static readonly int[] Imag8Regs = Enumerable.Range(2, 30)
        .Select(MOS6502Registers.RC)
        .ToArray();
    // Anyi8: any 8-bit register (A, X, Y, or Imag8). Used for values that can be
    // stored in any 8-bit location but aren't constrained to a specific
    // architectural register.
    //
    // This list defines the *membership* of the flexible class and the DEFAULT
    // (tie-break) preference order. It is deliberately zp-pool-FIRST: that is the
    // right default for a LONG-lived value, which should sit in the abundant
    // zero-page file and leave the three scarce architectural registers ($a/$x/$y)
    // free for the arithmetic chain (plan §2 lever #4, §3.5). The allocator's
    // cost-driven colour selection (GraphColouringAllocator.ColourPreferenceOrder)
    // overrides this order for SHORT-lived values, promoting $y/$x ahead of the zp
    // pool so a brief cross-instruction value lands in a real register the way the
    // llvm-mos references do (e.g. add-i16 parks its low result byte in $y).
    //
    // $y was added in register-allocator-redesign Phase 5 to close the "$y gap".
    // It competes with indexed addressing as the index register, but `mem.*`
    // indexed forms do not exist yet (plan §8); when they do, the instruction
    // selector's explicit `$y` operands already become fixed LiveIntervals
    // segments that interfere with any data vreg parked in $y, so data use of $y
    // cannot collide with an index need. $y is ordered LAST among the real
    // registers so $x (no index conflict) is preferred first.
    private static readonly int[] Anyi8Regs =
        [..Imag8Regs, MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y];

    public override ReadOnlySpan<int> GetAllocatableRegisters(int classId)
        => classId switch
        {
            MOS6502RegisterClass.Ac    => AcRegs,
            MOS6502RegisterClass.Xc    => XcRegs,
            MOS6502RegisterClass.Yc    => YcRegs,
            MOS6502RegisterClass.Cc    => CcRegs,
            MOS6502RegisterClass.Imag8 => Imag8Regs,
            MOS6502RegisterClass.Anyi8 => Anyi8Regs,
            _ => throw new ArgumentException($"Unknown register class {classId}", nameof(classId)),
        };

    public override bool BelongsToClass(int physReg, int classId)
        => MOS6502RegisterClass.OfPhysicalRegister(physReg) == classId;

    public override int ClassOfPhysicalRegister(int physReg)
        => MOS6502RegisterClass.OfPhysicalRegister(physReg);

    public override string  GetRegisterName(int register)   => MOS6502Registers.NameOf(register);
    public override string? GetRegisterClassName(int id)    => MOS6502RegisterClass.GetName(id);
    public override int?    ParseRegister(string name)      => MOS6502Registers.TryParse(name);
    public override int?    ParseRegisterClass(string name) =>
        MOS6502RegisterClass.TryParse(name, out var id) ? id : null;

    public override int FlexibleI8ClassId => MOS6502RegisterClass.Anyi8;

    // The general-purpose registers the post-RA RegisterScavengingPass may use
    // as copy scratch, in preference order ($a, then $x, then $y). The scavenger
    // picks the first that is DEAD at the copy's point, so an immediate-into-zp
    // copy emitted while $a holds a live parameter byte gets $x or $y instead —
    // no eviction, where the old RA scratch-clobber model forced $a free. See
    // MOS6502PseudoExpander (the only producer of scratch-needing copies today)
    // and RegisterScavengingPass (plan §3.6).
    public override ReadOnlySpan<int> GetScratchGprCandidates() => ScratchGprs;

    private static readonly int[] ScratchGprs =
        [MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y];

    // An $x↔$y copy can use $a (T-transfers) OR any free zero-page slot
    // (`STX $tmp ; LDY $tmp`); $x/$y themselves are the copy's ends. Prefer $a
    // (cheap T-transfers, no memory traffic) then the zp pool — a free zp slot
    // essentially always exists, so $x↔$y never needs to spill.
    private static readonly int[] XyScratch =
        [MOS6502Registers.A, .. Imag8Regs];

    // Per-shape scratch candidates: $x↔$y admits a zp bounce; every other
    // scratch-needing copy (zp→zp, immediate→zp) needs a GPR.
    public override ReadOnlySpan<int> GetScratchCandidates(MirInstruction scratchCopy)
    {
        if (scratchCopy.Operands is [PhysicalReg dst, PhysicalReg src, _]
            && IsXyPair(dst.Id, src.Id))
            return XyScratch;
        return ScratchGprs;
    }

    // Emergency save slots for the scavenger: the spare zero-page registers.
    // Saving a GPR to zero page (`ST? $zp`) and restoring it (`LD? $zp`) needs no
    // further scratch, so whichever zp slot is dead at the copy's point lets the
    // scavenger free up a GPR even when all three are live.
    public override ReadOnlySpan<int> GetEmergencyScratchSaveSlots() => Imag8Regs;

    private static bool IsXyPair(int a, int b) =>
        (a == MOS6502Registers.X && b == MOS6502Registers.Y) ||
        (a == MOS6502Registers.Y && b == MOS6502Registers.X);

    // The scarce architectural GPRs to prefer for SHORT-lived flexible values
    // (plan §3.5). Ordered $x, $y, $a:
    //   * $x / $y first because they are the natural home for a brief value that
    //     must survive across the next arithmetic instruction (the references park
    //     add-i16's relocated low result byte in $y while the high byte is added
    //     in $a). Interference excludes whichever of them is busy, so a value
    //     lands in the free one rather than the zero-page pool.
    //   * $a LAST: arithmetic (ADC/SBC/CMP/EOR …) is pinned to $a on this chip, so
    //     a value parked in $a almost always collides with the next chain link;
    //     keeping $a out of the short-range data preference leaves it free for the
    //     arithmetic it is needed for. ($a is still legal — it is in the class —
    //     it is merely the last GPR tried before the zp pool.)
    // Long-lived values ignore this list and take the class's zp-first default
    // order, keeping all three GPRs free for the arithmetic chain.
    public override ReadOnlySpan<int> GetShortRangeGprPreference() => ShortRangeGprs;

    private static readonly int[] ShortRangeGprs =
        [MOS6502Registers.X, MOS6502Registers.Y, MOS6502Registers.A];

    // The CC_MOS callee-saved registers: RC20..RC31 (zero page $14..$1F), the
    // cross-call register pool — the complement of the caller-saved RC2..RC19
    // scratch range. Matches MOS_CSR in llvm-mos. A clobbering callee saves and
    // restores these in its prologue/epilogue.
    public override ReadOnlySpan<int> GetCalleeSavedRegisters() => CalleeSavedRegs;

    private static readonly int[] CalleeSavedRegs = Enumerable.Range(20, 12)
        .Select(MOS6502Registers.RC)
        .ToArray();
}
