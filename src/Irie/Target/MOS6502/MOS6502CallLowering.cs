using Irie.Mir;

namespace Irie.Target.MOS6502;

// CC_MOS for the unified-MIR pipeline. Bytes are passed in A, X, RC2, RC3, …
// (RC0/RC1 reserved as the soft stack pointer); each multi-byte value occupies
// consecutive registers LSB-first. Mirrors the old Irie.Target.MOS6502.MOS6502CallLowering
// while emitting the new pseudo dialect ops on the MIR types.
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
    // independent of the call's arg/return registers. RC0/RC1 are reserved
    // as the soft stack pointer and survive across calls. RC2..RC15 are
    // listed here as the CC arg-passing slots; RC16..RC31 are intentionally
    // left out so the register allocator has a pool of "effectively
    // callee-saved" zero-page slots for values that need to live across
    // calls (until proper spilling / frame slots land in steps 11+, this
    // is the stopgap that lets simple cross-call patterns like
    // `call @F, %x; call @F, %x` allocate without spilling).
    //
    // Once frame slots + spilling are wired up, RC16..RC31 should be added
    // here so the CC matches LLVM-MOS conventions (all RC* caller-saved).
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
