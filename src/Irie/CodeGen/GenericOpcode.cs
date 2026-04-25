namespace Irie.CodeGen;

public static class GenericOpcode
{
    // Negative values distinguish generic opcodes from target opcodes (e.g. MOS6502 opcodes are in [0x00, 0xFF]).

    public const int GenericConstant            = -1;   // %0:iN = GenericConstant 42
    public const int GenericImplicitDefinition  = -2;   // %0:iN = GenericImplicitDefinition
    public const int GenericCopy                = -3;   // %0:iN = GenericCopy %1

    public const int GenericAdd                 = -10;
    public const int GenericSubtract            = -11;
    public const int GenericAnd                 = -12;
    public const int GenericOr                  = -13;
    public const int GenericXor                 = -14;
    public const int GenericShiftLeft           = -15;
    public const int GenericLogicalShiftRight   = -16;
    public const int GenericArithmeticShiftRight = -17;

    public const int GenericZeroExtend          = -20;
    public const int GenericSignExtend          = -21;
    public const int GenericTruncate            = -22;

    public const int GenericLoad                = -30;  // %0:iN = GenericLoad %ptr:i16
    public const int GenericStore               = -31;  // GenericStore %val, %ptr

    public const int GenericJump                = -40;  // GenericJump bb0
    public const int GenericBranchConditional   = -41;  // GenericBranchConditional %cond, bb_true, bb_false
    public const int GenericReturn              = -42;  // GenericReturn [%val]
    public const int GenericCall                = -50;  // [%0:iN =] GenericCall @symbol, %arg0, ...

    private static readonly Dictionary<int, string> _names = new()
    {
        [GenericConstant]             = "GenericConstant",
        [GenericImplicitDefinition]   = "GenericImplicitDefinition",
        [GenericCopy]                 = "GenericCopy",
        [GenericAdd]                  = "GenericAdd",
        [GenericSubtract]             = "GenericSubtract",
        [GenericAnd]                  = "GenericAnd",
        [GenericOr]                   = "GenericOr",
        [GenericXor]                  = "GenericXor",
        [GenericShiftLeft]            = "GenericShiftLeft",
        [GenericLogicalShiftRight]    = "GenericLogicalShiftRight",
        [GenericArithmeticShiftRight] = "GenericArithmeticShiftRight",
        [GenericZeroExtend]           = "GenericZeroExtend",
        [GenericSignExtend]           = "GenericSignExtend",
        [GenericTruncate]             = "GenericTruncate",
        [GenericLoad]                 = "GenericLoad",
        [GenericStore]                = "GenericStore",
        [GenericJump]                 = "GenericJump",
        [GenericBranchConditional]    = "GenericBranchConditional",
        [GenericReturn]               = "GenericReturn",
        [GenericCall]                 = "GenericCall",
    };

    private static readonly Dictionary<string, int> _byName =
        _names.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string? GetName(int opcode) =>
        _names.TryGetValue(opcode, out var name) ? name : null;

    public static bool TryParse(string name, out int opcode) =>
        _byName.TryGetValue(name, out opcode);

    public static bool IsGeneric(int opcode) => opcode < 0;
}
