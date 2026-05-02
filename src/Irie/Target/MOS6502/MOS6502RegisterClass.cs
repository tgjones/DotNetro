namespace Irie.Target.MOS6502;

// MOS6502 register class IDs. 0 means "no class assigned"; all real classes are >= 1.
//
// Mirrors llvm-mos's register classes (MOSRegisterInfo.td). Class assignment for MOS
// happens at InstructionSelect time (skipping RegBankSelect — see notes/codegen-followups.md
// and llvm-mos's MOSRegisterBankInfo.cpp, which puts every vreg into a single AnyRegBank).
public static class MOS6502RegisterClass
{
    public const int None  = 0;
    public const int Ac    = 1; // accumulator (A)
    public const int Xc    = 2; // X index register
    public const int Yc    = 3; // Y index register
    public const int Cc    = 4; // C flag (single bit)
    public const int Vc    = 5; // V flag (single bit)
    public const int Imag8 = 6; // imaginary 8-bit zero-page registers (RC0..RCn)

    public static string? GetName(int classId) => classId switch
    {
        Ac    => "Ac",
        Xc    => "Xc",
        Yc    => "Yc",
        Cc    => "Cc",
        Vc    => "Vc",
        Imag8 => "Imag8",
        _ => null,
    };

    public static bool TryParse(string name, out int classId)
    {
        classId = name switch
        {
            "Ac"    => Ac,
            "Xc"    => Xc,
            "Yc"    => Yc,
            "Cc"    => Cc,
            "Vc"    => Vc,
            "Imag8" => Imag8,
            _ => None,
        };
        return classId != None;
    }

    // Returns the class that a value held in the given physical register belongs to.
    // Used by selectors to constrain a vreg's class when copying from / to a phys reg.
    public static int OfPhysicalRegister(int physReg) => physReg switch
    {
        MOS6502Registers.A => Ac,
        MOS6502Registers.X => Xc,
        MOS6502Registers.Y => Yc,
        MOS6502Registers.C => Cc,
        _ when physReg >= MOS6502Registers.RC(0) => Imag8,
        _ => None,
    };
}
