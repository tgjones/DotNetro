using Irie.CodeGen;
using Irie.IR;

namespace Irie.Target.MOS6502;

// Implements the CC_MOS calling convention for 8-bit values:
//   Arguments/returns: A, X, RC2, RC3, RC4, RC5, RC6, RC7, RC8, ...
//   RC0/RC1 are reserved (soft stack pointer).
//   Each i32 is passed as 4 i8 bytes, LSB first.
public sealed class MOS6502CallLowering : CallLowering
{
    // The CC_MOS register sequence for i8 values (RC0/RC1 reserved).
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
        IRFunction irFunction,
        MachineFunction machineFunction,
        MachineBasicBlock entryBlock,
        MachineIRBuilder builder,
        Dictionary<IRValue, int> valueMap)
    {
        builder.SetInsertionPointAtEnd(entryBlock);

        var regIdx = 0;
        foreach (var arg in irFunction.Blocks[0].Arguments)
        {
            var byteCount = ByteCount(arg.Type);
            var byteVregs = new int[byteCount];

            for (var i = 0; i < byteCount; i++)
            {
                if (regIdx >= ArgRegs.Length)
                    throw new NotSupportedException(
                        "MOS6502CallLowering: argument exhausted available registers (stack args not yet supported).");

                var physReg = ArgRegs[regIdx++];
                builder.AddLiveIn(physReg);
                byteVregs[i] = builder.BuildCopyFromPhysReg(physReg, IRType.I8);
            }

            // Merge the bytes back into the IR-typed vreg so downstream translation
            // sees the argument at its original type.
            var argVreg = byteCount == 1
                ? byteVregs[0]
                : builder.BuildMerge(arg.Type, byteVregs);

            valueMap[arg] = argVreg;
        }
    }

    public override void LowerReturn(
        IRType irReturnType,
        int? returnValueVreg,
        MachineBasicBlock block,
        MachineIRBuilder builder)
    {
        builder.SetInsertionPointAtEnd(block);

        var implicitUses = new List<MachineOperand>();

        if (returnValueVreg.HasValue && irReturnType is not VoidType)
        {
            var byteCount = ByteCount(irReturnType);
            int[] byteVregs;

            if (byteCount == 1)
            {
                byteVregs = [returnValueVreg.Value];
            }
            else
            {
                byteVregs = builder.BuildUnmerge(IRType.I8, returnValueVreg.Value, byteCount);
            }

            for (var i = 0; i < byteCount; i++)
            {
                builder.BuildCopyToPhysReg(ArgRegs[i], byteVregs[i]);
                implicitUses.Add(new PhysicalRegisterOperand(ArgRegs[i], IsDefinition: false, IsImplicit: true));
            }
        }

        builder.BuildTargetInstr(MOS6502Opcode.RTS, [.. implicitUses]);
    }

    private static int ByteCount(IRType type) => type.SizeInBits / 8;
}
