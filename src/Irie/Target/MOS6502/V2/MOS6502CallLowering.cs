using Irie.IR;
using Irie.Mir;

namespace Irie.Target.MOS6502.V2;

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

    private static int ByteCount(IRType type) => type.SizeInBits / 8;

    private static void EnsureRegBudget(int needed)
    {
        if (needed > ArgRegs.Length)
            throw new NotSupportedException(
                "MOS6502CallLowering: argument list exceeds available CC_MOS registers (stack args not yet supported).");
    }
}
