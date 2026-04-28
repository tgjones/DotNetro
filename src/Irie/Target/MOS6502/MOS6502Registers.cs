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

    public static string NameOf(int regNum) => regNum switch
    {
        A => "A",
        X => "X",
        Y => "Y",
        S => "S",
        P => "P",
        C => "C",
        _ when regNum >= 6 => $"RC{regNum - 6}",
        _ => throw new ArgumentOutOfRangeException(nameof(regNum), $"Unknown register: {regNum}"),
    };
}
