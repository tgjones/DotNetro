using Irie.Mir;

namespace Irie.Target.MOS6502;

public sealed class MOS6502RegisterInfo : TargetRegisterInfo
{
    private static readonly int[] AClobber = [MOS6502Registers.A];

    private static readonly int[] AcRegs    = [MOS6502Registers.A];
    private static readonly int[] XcRegs    = [MOS6502Registers.X];
    private static readonly int[] YcRegs    = [MOS6502Registers.Y];
    private static readonly int[] CcRegs    = [MOS6502Registers.C];
    // RC0/RC1 reserved as soft stack pointer per CC_MOS; allocatable range is RC2..RC31.
    private static readonly int[] Imag8Regs = Enumerable.Range(2, 30)
        .Select(MOS6502Registers.RC)
        .ToArray();
    // Anyi8: any 8-bit register (A, X, or Imag8). Used for values that can be stored
    // in any 8-bit location but aren't constrained to a specific architectural register.
    // Order matters: the unified-MIR register allocator picks the first free physreg
    // by allocatable order, so we put the abundant imaginary RC* registers first and
    // save the single-physreg-class A/X for cases where Ac/Xc-class vregs need them.
    private static readonly int[] Anyi8Regs = [..Imag8Regs, MOS6502Registers.A, MOS6502Registers.X];

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

    // MOS6502PseudoExpander routes some `pseudo.copy` forms through $A as
    // scratch (it is the only register that can carry an immediate to a
    // zero-page slot, or bridge an X<->Y / zp<->zp move). Those forms destroy
    // whatever $A held even though $A is neither the copy's source nor its
    // destination, so RA must treat them as clobbering $A at the copy's slot.
    //
    // This hook runs *before* RA assigns physregs, so the destination is known
    // only by its register class and the source register is not yet pinned.
    // It must therefore be sound (never miss a real clobber) without being so
    // conservative that it strangles allocation.
    //
    // The only copy form that *unconditionally* routes through $A is an
    // `Immediate` materialised into anything other than $A itself:
    //   - Immediate -> zp        (LDA #imm ; STA $zp)   — clobbers $A
    //   - Immediate -> $X/$Y     (LDX/LDY #imm)         — does NOT touch $A
    //   - Immediate -> $A        (LDA #imm)             — $A is the def, no extra
    // An immediate has no register choice and the resulting copy can never be
    // coalesced away, so this clobber is always real.
    //
    // The register/zp -> zp forms (zp->zp, X<->Y) *can* also route through $A,
    // but only when the source/destination physregs force it — and a
    // constraint-fixup or relocation copy between two flexible vregs is usually
    // coalesced to an identity copy (emitting nothing) by allocation + copy
    // elimination. Marking those as $A clobbers pre-assignment falsely pins $A
    // out of reach of the very accumulator they feed (e.g. an ADC's $A operand
    // loaded immediately before a zp addend fixup copy), so they are left to
    // the allocator's physreg-reservation model and coalescing instead. A
    // value the allocator genuinely parks in $A across a real zp->zp copy is
    // already protected because that copy's source physreg is reserved (it
    // reads a live physreg), keeping an unrelated $A value from colliding.
    public override ReadOnlySpan<int> GetPseudoCopyScratchClobbers(
        MirOperand source, int srcClassId, int destClassId)
    {
        if (source is Immediate && destClassId != MOS6502RegisterClass.Ac
            && destClassId != MOS6502RegisterClass.Xc
            && destClassId != MOS6502RegisterClass.Yc)
        {
            // Immediate into a zp / flexible destination: LDA #imm ; STA $zp.
            // (Flexible may still land in $A or $X — but if it lands in zp the
            // clobber is real, and if it lands in $A/$X the copy's own def/use
            // covers $A; recording the clobber is safe either way.)
            return AClobber;
        }

        return default;
    }
}
