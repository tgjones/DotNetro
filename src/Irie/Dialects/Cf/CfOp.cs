namespace Irie.Dialects.Cf;

public enum CfOp : ushort
{
    // cf.br bb1
    Br,

    // cf.cond_br %cond, bb1, bb2(%x)
    CondBr,
}
