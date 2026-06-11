namespace Irie.Dialects.Cast;

public enum CastOp : ushort
{
    // %y : iM = cast.trunc %x : iN     (M < N)
    //   Truncate a wide integer to a narrower one by discarding the high bits.
    //   Operand layout: def[0] = result vreg (iM), use[0] = source vreg (iN).
    //
    // The legalizer lowers this to a byte-level slice of the source vreg:
    //   - If M = 8, it becomes a `pseudo.extract %x, 0` (a single byte).
    //   - Otherwise it becomes a `pseudo.unmerge` of %x into N/8 byte vregs,
    //     followed by a `pseudo.merge` of the low M/8 byte vregs into the
    //     result.
    Trunc,

    // %y : iN = cast.zext %x : iM     (M < N, zero-extend)
    //   Listed for completeness (plan §2.5). Not yet implemented — added
    //   when a test needs it.
    Zext,

    // %y : iN = cast.sext %x : iM     (M < N, sign-extend)
    //   Listed for completeness. Needs arith.asr (or equivalent) to broadcast
    //   the top bit; not yet in the arith dialect.
    Sext,
}
