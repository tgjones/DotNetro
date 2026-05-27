namespace Irie.Target.MOS6502;

public static class MOS6502Registers
{
    public const int A = 0;
    public const int X = 1;
    public const int Y = 2;
    public const int S = 3;
    public const int P = 4;
    public const int C = 5; // carry flag

    // Imaginary zero-page registers: RC0=6, RC1=7, RC2=8, ...
    public static int RC(int n) => 6 + n;

    // Status-flag physical registers added for the new MIR dialect (used by
    // mos6502.cmp, mos6502.bgt, etc.). IDs sit in a high range so they don't
    // collide with the existing RC(n) range (which is used by old CodeGen
    // CallLowering). The unified-IR plan §6 will eventually shift everything
    // into a contiguous layout once old CodeGen is retired.
    public const int N = 64;
    public const int V = 65;
    public const int Z = 66;
    public const int I = 67;
    public const int D = 68;
    public const int B = 69;

    public static string NameOf(int regNum) => regNum switch
    {
        A => "A",
        X => "X",
        Y => "Y",
        S => "S",
        P => "P",
        C => "C",
        N => "N",
        V => "V",
        Z => "Z",
        I => "I",
        D => "D",
        B => "B",
        _ when regNum >= 6 && regNum < 64 => $"RC{regNum - 6}",
        _ => throw new ArgumentOutOfRangeException(nameof(regNum), $"Unknown register: {regNum}"),
    };

    public static int? TryParse(string name) => name switch
    {
        "A" => A,
        "X" => X,
        "Y" => Y,
        "S" => S,
        "P" => P,
        "C" => C,
        "N" => N,
        "V" => V,
        "Z" => Z,
        "I" => I,
        "D" => D,
        "B" => B,
        _ when name.Length > 2 && name.StartsWith("RC") && int.TryParse(name[2..], out var n) => RC(n),
        _ => null,
    };
}
