using Irie.CodeGen;
using Irie.IR;

namespace Irie.Target.MOS6502;

// Selects generic MachineIR instructions to MOS6502 target instructions.
//
// Merge/Unmerge are logical groupings with no real instruction; the selector
// tracks which wide vregs are composed of which narrow vregs (_mergeMap) and
// expands Unmerge into GenericCopy chains through that map.
public sealed class MOS6502InstructionSelector : InstructionSelector
{
    // Maps a wide vreg to its component narrow vregs (populated by GenericMerge selection).
    private readonly Dictionary<int, int[]> _mergeMap = [];

    public override void BeginFunction(MachineFunction function) => _mergeMap.Clear();

    public override bool Select(MachineInstruction instruction, MachineIRBuilder builder)
    {
        switch (instruction.Opcode)
        {
            case GenericOpcode.GenericCopy:
                return true;

            case GenericOpcode.GenericConstant:
                return SelectConstant(instruction, builder);

            case GenericOpcode.GenericMerge:
                return SelectMerge(instruction, builder);

            case GenericOpcode.GenericUnmerge:
                return SelectUnmerge(instruction, builder);

            case GenericOpcode.GenericAddCarry:
                return SelectAddCarry(instruction, builder);

            default:
                // Already-selected target instructions (non-negative opcodes) pass through.
                return !GenericOpcode.IsGeneric(instruction.Opcode);
        }
    }

    private bool SelectMerge(MachineInstruction instr, MachineIRBuilder builder)
    {
        var def = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .First(o => o.IsDefinition);
        var components = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .Where(o => !o.IsDefinition)
            .Select(o => o.VirtualRegister)
            .ToArray();
        _mergeMap[def.VirtualRegister] = components;
        builder.Remove(instr);
        return true;
    }

    private bool SelectUnmerge(MachineInstruction instr, MachineIRBuilder builder)
    {
        var defs = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .Where(o => o.IsDefinition)
            .ToArray();
        var source = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .First(o => !o.IsDefinition)
            .VirtualRegister;

        if (_mergeMap.TryGetValue(source, out var components))
        {
            builder.SetInsertionPointBefore(instr);
            for (var i = 0; i < defs.Length; i++)
                builder.BuildCopyVirtualToVirtual(defs[i].VirtualRegister, components[i]);
        }
        // If the source has no merge map entry it remains as-is; a later pass
        // (e.g. dead-code elimination) would clean up the dangling reference.

        builder.Remove(instr);
        return true;
    }

    private bool SelectAddCarry(MachineInstruction instr, MachineIRBuilder builder)
    {
        // Operand layout (defs first):
        //   def[0]: result vreg (i8)
        //   def[1]: carry_out vreg (i1)
        //   use[0]: a            → ADC L (class Ac)
        //   use[1]: b            → ADC R (class Imag8)
        //   use[2]: carry_in     → ADC carry_in (class Cc)
        //
        // Every AddCarry now has a uniform 3-use shape: the legalizer materializes
        // the chain-head carry-in as `GenericConstant i1 0`, which the selector
        // lowers to `LDImm1 0` (a target pseudo whose def is class Cc).

        var defs = instr.Operands.Where(IsVRegDef).Cast<VirtualRegisterOperand>().ToArray();
        var uses = instr.Operands.Where(o => !IsVRegDef(o)).ToArray();

        var resultVreg   = defs[0].VirtualRegister;
        var carryOutVreg = defs[1].VirtualRegister;

        builder.SetInsertionPointBefore(instr);

        var newDefs = builder.BuildTargetInstructionWithDefinitions(
            MOS6502Opcode.ADC_ZeroPage,
            [IRType.I8, IRType.I1],
            uses,
            MOS6502InstructionInfo.Instance.Get(MOS6502Opcode.ADC_ZeroPage).OperandClasses);

        builder.Function.ReplaceAllUsesOfRegister(resultVreg,   newDefs[0]);
        builder.Function.ReplaceAllUsesOfRegister(carryOutVreg, newDefs[1]);

        builder.Remove(instr);
        return true;
    }

    private bool SelectConstant(MachineInstruction instr, MachineIRBuilder builder)
    {
        // Operand layout: def[0]: vreg (typed), use[0]: ImmediateOperand.
        // Currently only i1 is supported (carry-flag materialization for AddCarry).
        var def = (VirtualRegisterOperand)instr.Operands[0];
        var imm = (ImmediateOperand)instr.Operands[1];
        var type = builder.Function.GetVirtualRegisterType(def.VirtualRegister);

        if (type != IRType.I1)
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: GenericConstant of type {type.DisplayName} is not yet supported.");

        builder.SetInsertionPointBefore(instr);

        var newDef = builder.BuildTargetInstructionWithDefinition(
            MOS6502Opcode.LDImm1,
            IRType.I1,
            [imm],
            MOS6502InstructionInfo.Instance.Get(MOS6502Opcode.LDImm1).OperandClasses);

        builder.Function.ReplaceAllUsesOfRegister(def.VirtualRegister, newDef);

        builder.Remove(instr);
        return true;
    }

    private static bool IsVRegDef(MachineOperand o) =>
        o is VirtualRegisterOperand v && v.IsDefinition;
}
