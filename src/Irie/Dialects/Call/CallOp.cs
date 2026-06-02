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

    // %r0, %r1, ... = call.indirect %target, %arg0, %arg1, ...
    //
    // Call through an i16 function pointer. Operand layout:
    //   defs[0..N-1]: return-value vregs (typed, in CC order)
    //   uses[0]:      i16 target-pointer vreg
    //   uses[1..M]:   arg vregs (typed, in CC order)
    //
    // The frontend lowers IL `callvirt` to a vtable load + this op (plan
    // §2.3 / §4.8). AbiLowering routes through CallLowering.LowerIndirectCall
    // which on MOS6502 parks the pointer in fixed zero-page slots and jumps
    // to a runtime trampoline (`@__call_indirect_trampoline`) that performs
    // the indirect JMP. Not a terminator.
    Indirect,
}
