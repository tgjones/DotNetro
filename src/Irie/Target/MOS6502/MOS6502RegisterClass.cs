namespace Irie.Target.MOS6502;

// MOS6502 register class IDs. 0 means "no class assigned"; all real classes are >= 1.
//
// - Ac, Xc, Yc, Cc, Vc, Nc, Zc, Ic, Dc, Bc: single architectural registers
// - Imag8: imaginary 8-bit zero-page registers (RC0..RCn)
// - Anyi8: any 8-bit register (A, X, Y, or Imag8). Used for values that can be
//   stored in any 8-bit location but aren't constrained to a specific one. $y was
//   added to this class in register-allocator-redesign Phase 5 (plan §3.5): the
//   llvm-mos references use $a/$x/$y freely for data, whereas Irie previously
//   parked everything in the zero-page pool and never touched $y.
public static class MOS6502RegisterClass
{
    public const int None  = 0;
    public const int Ac    = 1; // accumulator (A)
    public const int Xc    = 2; // X index register
    public const int Yc    = 3; // Y index register
    public const int Cc    = 4; // C flag (single bit)
    public const int Vc    = 5; // V flag (single bit)
    public const int Imag8 = 6; // imaginary 8-bit zero-page registers (RC0..RCn)
    public const int Anyi8 = 7; // any 8-bit register (A, X, Y, or Imag8)
    // Axy: any real architectural 8-bit register (A, X, or Y) — but NOT a
    // zero-page imaginary register. The source class for an absolute store, where
    // all three of STA/STX/STY exist: the value may live in whichever of A/X/Y RA
    // finds cheapest (driven by coalescing with the byte's producer), and the
    // post-RA MOS6502AddressingModeSelectorPass refines `sta.abs` → `stx.abs` /
    // `sty.abs` to match. Unlike Anyi8 it deliberately excludes the zp pool: a zp
    // value cannot be a direct store source, so admitting it would force a
    // copy-through-A and defeat the point. Mirrors llvm-mos's `GPR`-classed
    // store-value operand on `STAbs`.
    // Status-flag classes for the new MIR dialect (one class per flag is the
    // first-cut layout per unified-IR plan §6 / open question #2; can be unified
    // later if RA freedom is never needed).
    public const int Nc    = 8;  // N flag
    public const int Zc    = 9;  // Z flag
    public const int Ic    = 10; // I flag (interrupt disable)
    public const int Dc    = 11; // D flag (decimal mode)
    public const int Bc    = 12; // B flag (break)
    public const int Axy   = 13; // any real register (A, X, or Y); excludes zp

    public static string? GetName(int classId) => classId switch
    {
        Ac    => "ac",
        Xc    => "xc",
        Yc    => "yc",
        Cc    => "cc",
        Vc    => "vc",
        Imag8 => "zp",
        Anyi8 => "any8",
        Axy   => "axy",
        Nc    => "nc",
        Zc    => "zc",
        Ic    => "ic",
        Dc    => "dc",
        Bc    => "bc",
        _ => null,
    };

    public static bool TryParse(string name, out int classId)
    {
        classId = name switch
        {
            "ac"    => Ac,
            "xc"    => Xc,
            "yc"    => Yc,
            "cc"    => Cc,
            "vc"    => Vc,
            "zp"    => Imag8,
            "any8"  => Anyi8,
            "axy"   => Axy,
            "nc"    => Nc,
            "zc"    => Zc,
            "ic"    => Ic,
            "dc"    => Dc,
            "bc"    => Bc,
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
        MOS6502Registers.N => Nc,
        MOS6502Registers.V => Vc,
        MOS6502Registers.Z => Zc,
        MOS6502Registers.I => Ic,
        MOS6502Registers.D => Dc,
        MOS6502Registers.B => Bc,
        _ when physReg >= MOS6502Registers.RC(0) => Imag8,
        _ => None,
    };
}
