using Irie.Mir;

namespace Irie.Target.MOS6502;

// CC_MOS for the unified-MIR pipeline. Bytes are passed/returned in A, X, RC2,
// RC3, … (LSB-first for multi-byte values); RC0/RC1 are the soft stack pointer.
// Mirrors the old Irie.Target.MOS6502.MOS6502CallLowering while emitting the new
// pseudo dialect ops on the MIR types.
//
// Register save classes follow the LLVM-MOS C calling convention
// (https://llvm-mos.org/wiki/C_calling_convention):
//
//   Caller-saved (volatile; a callee may clobber these freely, so the caller
//   must preserve any it still needs across a call):
//       A, X, Y, C, N, V, Z, RC2..RC19   (RC2..RC19 = zp $02..$13)
//
//   Callee-saved (a callee that clobbers these must save+restore them, so the
//   caller can rely on them surviving a call):
//       PC, S, D, I, RC0, RC1, RC20..RC31   (RC20..RC31 = zp $14..$1F)
//
// CAVEAT — the current implementation does NOT yet match this convention:
// `CallerSavedScratch` below lists RC2..RC15 (not RC2..RC19) and wrongly
// includes D and I, and no function actually saves/restores the callee-saved
// RC20..RC31, so cross-call values placed there are not yet preserved. Aligning
// the clobber set to the table above, plus callee-saved spilling to a static
// frame, is the subject of notes/static-stack-alloc-plan.md (Layers 0–3). Until
// that lands, RC16..RC29 are left out of CallerSavedScratch as a stopgap (see
// the CallerSavedScratch comment) which preserves simple cross-call patterns but
// miscompiles when a callee reuses those slots (e.g. CallNestedMethods).
public sealed class MOS6502CallLowering : Irie.Target.CallLowering
{
    private static readonly int[] ArgRegs =
    [
        MOS6502Registers.A,
        MOS6502Registers.X,
        MOS6502Registers.RC(2),
        MOS6502Registers.RC(3),
        MOS6502Registers.RC(4),
        MOS6502Registers.RC(5),
        MOS6502Registers.RC(6),
        MOS6502Registers.RC(7),
        MOS6502Registers.RC(8),
        MOS6502Registers.RC(9),
        MOS6502Registers.RC(10),
        MOS6502Registers.RC(11),
        MOS6502Registers.RC(12),
        MOS6502Registers.RC(13),
        MOS6502Registers.RC(14),
        MOS6502Registers.RC(15),
    ];

    public override void LowerFormalArguments(
        MirFunction function,
        MirBlock entryBlock,
        int[] originalParameters,
        MirBuilder builder)
    {
        var regIdx = 0;
        foreach (var paramVreg in originalParameters)
        {
            var annotation = function.GetVRegAnnotation(paramVreg);
            if (annotation is not TypedVReg typed)
                throw new InvalidOperationException(
                    $"AbiLowering: entry-block parameter %{paramVreg} has non-typed annotation {annotation}");

            var byteCount = ByteCount(typed.Type);
            EnsureRegBudget(regIdx + byteCount);

            if (byteCount == 1)
            {
                var physReg = ArgRegs[regIdx++];
                builder.AddLiveIn(physReg);
                builder.BuildCopyFromPhysicalRegisterInto(paramVreg, physReg);
                continue;
            }

            var byteVregs = new int[byteCount];
            for (var i = 0; i < byteCount; i++)
            {
                var physReg = ArgRegs[regIdx++];
                builder.AddLiveIn(physReg);
                byteVregs[i] = builder.BuildCopyFromPhysicalRegister(physReg, IRType.I8);
            }
            builder.BuildMergeInto(paramVreg, byteVregs);
        }
    }

    public override void LowerReturn(
        IRType returnType,
        int? returnValueVreg,
        MirBuilder builder)
    {
        if (returnType is VoidType || !returnValueVreg.HasValue)
            return;

        var byteCount = ByteCount(returnType);
        EnsureRegBudget(byteCount);

        var byteVregs = byteCount == 1
            ? [returnValueVreg.Value]
            : builder.BuildUnmerge(IRType.I8, returnValueVreg.Value, byteCount);

        for (var i = 0; i < byteCount; i++)
            builder.BuildCopyToPhysicalRegister(ArgRegs[i], byteVregs[i]);
    }

    public override void LowerCall(
        string calleeName,
        IRType[] argTypes,
        int[] argVregs,
        IRType[] returnTypes,
        int[] returnVregs,
        MirBuilder builder)
    {
        // Place each arg into its CC physreg(s) in CC order (LSB-first for
        // multi-byte values), using the same ArgRegs sequence as formal
        // argument lowering. Track which physregs got args so we can mark
        // them as implicit uses on the JSR.
        var implicitUses = new List<int>();
        var regIdx = 0;
        for (var i = 0; i < argVregs.Length; i++)
        {
            var byteCount = ByteCount(argTypes[i]);
            EnsureRegBudget(regIdx + byteCount);

            int[] byteVregs;
            if (byteCount == 1)
            {
                byteVregs = [argVregs[i]];
            }
            else
            {
                byteVregs = builder.BuildUnmerge(IRType.I8, argVregs[i], byteCount);
            }

            for (var b = 0; b < byteCount; b++)
            {
                var physReg = ArgRegs[regIdx++];
                builder.BuildCopyToPhysicalRegister(physReg, byteVregs[b]);
                implicitUses.Add(physReg);
            }
        }

        // Build the jsr operand array: Symbol callee first, then implicit-uses
        // for arg physregs, then implicit-defs for return physregs + every
        // caller-saved scratch physreg.
        var operands = new List<MirOperand>
        {
            new Symbol(calleeName),
        };

        // Implicit-uses on arg physregs: tells RA that those are live INTO
        // the jsr.
        foreach (var physReg in implicitUses)
            operands.Add(new PhysicalReg(physReg, IsDefinition: false, IsImplicit: true));

        // Implicit-defs on return physregs + all caller-saved scratch
        // (clobber barrier). Track the set so we don't add a register twice.
        // CC_MOS treats $a, $x, all RC2..RC15 as caller-saved; $y, $c, $n,
        // $z, $v, $i, $d, $b are also clobbered by an arbitrary callee.
        var clobberedDefs = new HashSet<int>(CallerSavedScratch);
        var returnRegIdx = 0;
        var returnPhysRegs = new List<int>();
        for (var i = 0; i < returnVregs.Length; i++)
        {
            var byteCount = ByteCount(returnTypes[i]);
            EnsureRegBudget(returnRegIdx + byteCount);
            for (var b = 0; b < byteCount; b++)
            {
                var physReg = ArgRegs[returnRegIdx++];
                returnPhysRegs.Add(physReg);
                clobberedDefs.Add(physReg);
            }
        }

        foreach (var physReg in clobberedDefs.OrderBy(r => r))
            operands.Add(new PhysicalReg(physReg, IsDefinition: true, IsImplicit: true));

        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JsrAbs), operands.ToArray());

        // Copy each return-byte physreg into the destination vreg(s), in CC
        // order. For multi-byte returns, merge the bytes back together.
        returnRegIdx = 0;
        for (var i = 0; i < returnVregs.Length; i++)
        {
            var byteCount = ByteCount(returnTypes[i]);
            if (byteCount == 1)
            {
                builder.BuildCopyFromPhysicalRegisterInto(returnVregs[i], ArgRegs[returnRegIdx++]);
                continue;
            }

            var byteVregs = new int[byteCount];
            for (var b = 0; b < byteCount; b++)
                byteVregs[b] = builder.BuildCopyFromPhysicalRegister(ArgRegs[returnRegIdx++], IRType.I8);
            builder.BuildMergeInto(returnVregs[i], byteVregs);
        }
    }

    public override void LowerIndirectCall(
        int targetPtrVreg,
        IRType[] argTypes,
        int[] argVregs,
        IRType[] returnTypes,
        int[] returnVregs,
        MirBuilder builder)
    {
        // Indirect call shim: park the function pointer's two bytes in the
        // reserved zero-page slots @__call_target_lo / @__call_target_hi (RC30
        // / RC31 by convention), then jsr.abs into the @__call_indirect_trampoline
        // runtime helper which performs `JMP (zp)` to actually dispatch. The
        // trampoline lives in runtime.mir (hand-written; written in a later
        // step). From the caller's perspective the lowering is otherwise
        // identical to a direct call to the trampoline.

        // Args setup (same as direct call).
        var implicitUses = new List<int>();
        var regIdx = 0;
        for (var i = 0; i < argVregs.Length; i++)
        {
            var byteCount = ByteCount(argTypes[i]);
            EnsureRegBudget(regIdx + byteCount);

            int[] byteVregs = byteCount == 1
                ? [argVregs[i]]
                : builder.BuildUnmerge(IRType.I8, argVregs[i], byteCount);

            for (var b = 0; b < byteCount; b++)
            {
                var physReg = ArgRegs[regIdx++];
                builder.BuildCopyToPhysicalRegister(physReg, byteVregs[b]);
                implicitUses.Add(physReg);
            }
        }

        // Park the pointer bytes in the trampoline's fixed zp slots.
        var ptrBytes = builder.BuildUnmerge(IRType.I8, targetPtrVreg, 2);
        builder.BuildCopyToPhysicalRegister(CallTargetLo, ptrBytes[0]);
        builder.BuildCopyToPhysicalRegister(CallTargetHi, ptrBytes[1]);
        implicitUses.Add(CallTargetLo);
        implicitUses.Add(CallTargetHi);

        // Build the jsr operand array against the trampoline symbol.
        var operands = new List<MirOperand>
        {
            new Symbol("__call_indirect_trampoline"),
        };

        foreach (var physReg in implicitUses)
            operands.Add(new PhysicalReg(physReg, IsDefinition: false, IsImplicit: true));

        var clobberedDefs = new HashSet<int>(CallerSavedScratch);
        // The trampoline reads the pointer slots; treat them as clobbered too
        // (they're scratch, the next call can re-init them).
        clobberedDefs.Add(CallTargetLo);
        clobberedDefs.Add(CallTargetHi);

        var returnRegIdx = 0;
        for (var i = 0; i < returnVregs.Length; i++)
        {
            var byteCount = ByteCount(returnTypes[i]);
            EnsureRegBudget(returnRegIdx + byteCount);
            for (var b = 0; b < byteCount; b++)
                clobberedDefs.Add(ArgRegs[returnRegIdx++]);
        }

        foreach (var physReg in clobberedDefs.OrderBy(r => r))
            operands.Add(new PhysicalReg(physReg, IsDefinition: true, IsImplicit: true));

        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JsrAbs), operands.ToArray());

        // Returns out.
        returnRegIdx = 0;
        for (var i = 0; i < returnVregs.Length; i++)
        {
            var byteCount = ByteCount(returnTypes[i]);
            if (byteCount == 1)
            {
                builder.BuildCopyFromPhysicalRegisterInto(returnVregs[i], ArgRegs[returnRegIdx++]);
                continue;
            }

            var byteVregs = new int[byteCount];
            for (var b = 0; b < byteCount; b++)
                byteVregs[b] = builder.BuildCopyFromPhysicalRegister(ArgRegs[returnRegIdx++], IRType.I8);
            builder.BuildMergeInto(returnVregs[i], byteVregs);
        }
    }

    // Reserved zero-page slots that the indirect-call trampoline reads its
    // target address from. The runtime trampoline (`@__call_indirect_trampoline`)
    // does `JMP ($1E)` — i.e. reads address $1E / $1F as a 16-bit pointer and
    // jumps there. The slots are out of the CC arg range and out of the Imag8
    // allocatable pool's first 16 entries (RC2..RC15 are reserved as callee-
    // saved scratch in MOS6502CallLowering's comment).
    private static readonly int CallTargetLo = MOS6502Registers.RC(30);
    private static readonly int CallTargetHi = MOS6502Registers.RC(31);

    // Caller-saved scratch physregs that any arbitrary callee may clobber,
    // independent of the call's arg/return registers. See the file-header
    // table for the full LLVM-MOS save classes.
    //
    // TARGET convention: caller-saved = A, X, Y, C, N, V, Z, RC2..RC19;
    // callee-saved = RC0, RC1, RC20..RC31 (plus D, I, S, PC). I.e. the flags
    // D and I should NOT be here, and the RC caller-saved range should run to
    // RC19, not RC15.
    //
    // CURRENT (stopgap) convention, which this list encodes: only RC2..RC15 are
    // marked clobbered, and RC16..RC31 are left out so the RA has a pool of
    // "effectively callee-saved" zero-page slots for values that must live
    // across calls — without any callee actually saving them. This lets simple
    // cross-call patterns (`call @F, %x; call @F, %x`) allocate without
    // spilling, but MISCOMPILES when a callee reuses those slots (the
    // CallNestedMethods bug). D and I are also (incorrectly) included here.
    //
    // Fixing this — extend the clobber set to RC2..RC19, drop D/I, and add
    // callee-saved spilling of RC20..RC31 to a static frame — is Layers 0–1 of
    // notes/static-stack-alloc-plan.md.
    private static readonly int[] CallerSavedScratch =
    [
        MOS6502Registers.A,
        MOS6502Registers.X,
        MOS6502Registers.Y,
        MOS6502Registers.C, MOS6502Registers.N, MOS6502Registers.Z,
        MOS6502Registers.V, MOS6502Registers.I, MOS6502Registers.D,
        MOS6502Registers.B,
        MOS6502Registers.RC(2),  MOS6502Registers.RC(3),
        MOS6502Registers.RC(4),  MOS6502Registers.RC(5),
        MOS6502Registers.RC(6),  MOS6502Registers.RC(7),
        MOS6502Registers.RC(8),  MOS6502Registers.RC(9),
        MOS6502Registers.RC(10), MOS6502Registers.RC(11),
        MOS6502Registers.RC(12), MOS6502Registers.RC(13),
        MOS6502Registers.RC(14), MOS6502Registers.RC(15),
    ];

    private static int ByteCount(IRType type) => type.SizeInBits / 8;

    private static void EnsureRegBudget(int needed)
    {
        if (needed > ArgRegs.Length)
            throw new NotSupportedException(
                "MOS6502CallLowering: argument list exceeds available CC_MOS registers (stack args not yet supported).");
    }
}
