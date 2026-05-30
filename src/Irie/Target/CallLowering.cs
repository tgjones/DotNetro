using Irie.Mir;

namespace Irie.Target;

// MIR-shaped calling-convention lowering. The target-specific implementation
// rewrites a MirFunction's entry-block parameters into `pseudo.copy` from
// live-in physregs (plus a `pseudo.merge` for multi-byte values), and each
// `core.return` into per-byte `pseudo.copy` to result physregs followed by
// `pseudo.return`. See AbiLoweringPass for the orchestration.
public abstract class CallLowering
{
    // Called once per function. `originalParameters` is the entry block's
    // parameter vreg list captured before the block was cleared; the
    // implementation should leave each parameter vreg fully defined (via copies
    // from physregs, optionally merged) at the builder's current insertion
    // point on the entry block.
    public abstract void LowerFormalArguments(
        MirFunction function,
        MirBlock entryBlock,
        int[] originalParameters,
        MirBuilder builder);

    // Called for each `core.return`. The implementation should emit per-byte
    // `pseudo.copy` to result physregs at the builder's current insertion
    // point; the surrounding pass replaces the `core.return` with a
    // `pseudo.return` after this method returns.
    public abstract void LowerReturn(
        IRType returnType,
        int? returnValueVreg,
        MirBuilder builder);
}
