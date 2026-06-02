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

    // Called for each `call.func` instruction. The implementation should
    // replace the call.func with:
    //   1. Per-arg-byte `pseudo.copy <argByteVreg> → <argPhysReg>` setup
    //      (preceded by `pseudo.unmerge` for wide arg types).
    //   2. A target-specific call instruction (e.g. `mos6502.jsr.abs @callee`)
    //      with implicit-uses on arg physregs and implicit-defs on return
    //      physregs + caller-saved scratch physregs.
    //   3. Per-return-byte `pseudo.copy <returnPhysReg> → <returnByteVreg>`
    //      teardown (followed by `pseudo.merge` for wide return types).
    //
    // The `call.func` is removed by the calling pass after this method
    // returns. argVregs and returnVregs are the vreg IDs from the call.func
    // operand list (in MIR text order).
    public abstract void LowerCall(
        string calleeName,
        IRType[] argTypes,
        int[] argVregs,
        IRType[] returnTypes,
        int[] returnVregs,
        MirBuilder builder);
}
