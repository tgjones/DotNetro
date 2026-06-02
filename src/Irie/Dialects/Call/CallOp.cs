namespace Irie.Dialects.Call;

public enum CallOp : ushort
{
    // %r0, %r1, ... = call.func @callee, %arg0, %arg1, ...
    //
    // Direct call to a named function. Operand layout:
    //   defs[0..N-1]: return-value vregs (typed, in CC order)
    //   uses[0]:      Symbol("callee")
    //   uses[1..M]:   arg vregs (typed, in CC order)
    //
    // The op survives through AbiLowering, which delegates to
    // CallLowering.LowerCall to translate it into per-arg pseudo.copy →
    // physreg setups + mos6502.jsr.abs + per-return pseudo.copy ← physreg
    // teardowns. Not a terminator — control returns to the next instruction.
    Func,
}
