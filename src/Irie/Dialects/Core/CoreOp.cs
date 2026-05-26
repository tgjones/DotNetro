namespace Irie.Dialects.Core;

public enum CoreOp : ushort
{
    // core.return %v  — source-level return; lowered to pseudo.return by
    // AbiLoweringPass.
    Return,
}
