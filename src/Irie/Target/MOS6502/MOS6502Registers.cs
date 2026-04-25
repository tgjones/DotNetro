namespace Irie.Target.MOS6502;

public static class MOS6502Registers
{
    public const int A = 0;
    public const int X = 1;
    public const int Y = 2;
    public const int S = 3;
    public const int P = 4;

    public static string NameOf(int regNum) => regNum switch
    {
        A => "A",
        X => "X",
        Y => "Y",
        S => "S",
        P => "P",
        _ => throw new ArgumentOutOfRangeException(nameof(regNum), $"Unknown register: {regNum}"),
    };
}
