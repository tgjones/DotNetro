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

            case GenericOpcode.GenericMerge:
                return SelectMerge(instruction, builder);

            case GenericOpcode.GenericUnmerge:
                return SelectUnmerge(instruction, builder);

            case GenericOpcode.GenericAddCarry:
                return SelectAddCarry(instruction, builder);

            case GenericOpcode.GenericReturn:
                builder.SetInsertionPointBefore(instruction);
                builder.BuildTargetInstr(MOS6502Opcode.RTS);
                builder.Remove(instruction);
                return true;

            default:
                return false;
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
                builder.BuildCopyVirtToVirt(defs[i].VirtualRegister, components[i]);
        }
        // If the source has no merge map entry it remains as-is; a later pass
        // (e.g. dead-code elimination) would clean up the dangling reference.

        builder.Remove(instr);
        return true;
    }

    private bool SelectAddCarry(MachineInstruction instr, MachineIRBuilder builder)
    {
        // Operand layout (defs first):
        //   def[0]: result vreg
        //   def[1]: carry_out vreg  (becomes implicit C flag after selection)
        //   use[0]: a
        //   use[1]: b
        //   use[2]: carry_in — either ImmediateOperand(0) or VirtualRegisterOperand

        var defs = instr.Operands.Where(IsVRegDef).Cast<VirtualRegisterOperand>().ToArray();
        var uses = instr.Operands.Where(o => !IsVRegDef(o)).ToArray();

        var resultVreg = defs[0].VirtualRegister;
        var aVreg = ((VirtualRegisterOperand)uses[0]).VirtualRegister;
        var bVreg = ((VirtualRegisterOperand)uses[1]).VirtualRegister;
        var carryIn = uses[2];

        builder.SetInsertionPointBefore(instr);

        // Immediate 0 carry-in means this is the start of a carry chain: emit CLC.
        if (carryIn is ImmediateOperand { Value: 0 })
            builder.BuildTargetInstr(MOS6502Opcode.CLC);

        // ADC with two virtual-register operands (addressing mode resolved post-RA).
        builder.BuildTargetInstrWithDef(MOS6502Opcode.ADC_ZeroPage, IRType.I8,
            new VirtualRegisterOperand(aVreg, IsDefinition: false),
            new VirtualRegisterOperand(bVreg, IsDefinition: false));

        // The carry_out vreg (defs[1]) becomes dead; subsequent GenericAddCarrys that
        // reference it as carry_in (vreg form) are implicitly chained via the 6502 C flag.

        builder.Remove(instr);
        return true;
    }

    private static bool IsVRegDef(MachineOperand o) =>
        o is VirtualRegisterOperand v && v.IsDefinition;
}
