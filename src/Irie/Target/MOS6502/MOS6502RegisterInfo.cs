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

    public override string  GetRegisterName(int register)   => MOS6502Registers.NameOf(register);
    public override string? GetRegisterClassName(int id)    => MOS6502RegisterClass.GetName(id);
    public override int?    ParseRegister(string name)      => MOS6502Registers.TryParse(name);
    public override int?    ParseRegisterClass(string name) =>
        MOS6502RegisterClass.TryParse(name, out var id) ? id : null;

    public override int FlexibleI8ClassId => MOS6502RegisterClass.Anyi8;
}
