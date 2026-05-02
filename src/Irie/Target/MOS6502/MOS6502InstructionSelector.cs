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
        //   use[0]: a
        //   use[1]: b
        //   use[2]: carry_in — either ImmediateOperand(0) or VirtualRegisterOperand
        //
        // ADC_ZeroPage exposes both result and carry_out as SSA defs, and (for non-
        // initial members of a carry chain) takes the previous carry_out as an
        // explicit use, so the carry chain is visible in the IR. The initial
        // carry-in 0 case still uses an inline CLC; see notes/codegen-followups.md
        // for why we don't yet materialise it as an SSA constant.

        var defs = instr.Operands.Where(IsVRegDef).Cast<VirtualRegisterOperand>().ToArray();
        var uses = instr.Operands.Where(o => !IsVRegDef(o)).ToArray();

        var resultVreg   = defs[0].VirtualRegister;
        var carryOutVreg = defs[1].VirtualRegister;
        var aVreg = ((VirtualRegisterOperand)uses[0]).VirtualRegister;
        var bVreg = ((VirtualRegisterOperand)uses[1]).VirtualRegister;
        var carryIn = uses[2];

        builder.SetInsertionPointBefore(instr);

        var adcUses = new List<MachineOperand>
        {
            new VirtualRegisterOperand(aVreg, IsDefinition: false),
            new VirtualRegisterOperand(bVreg, IsDefinition: false),
        };

        if (carryIn is ImmediateOperand { Value: 0 })
        {
            builder.BuildTargetInstruction(MOS6502Opcode.CLC);
        }
        else
        {
            adcUses.Add(carryIn);
        }

        var newDefs = builder.BuildTargetInstructionWithDefinitions(
            MOS6502Opcode.ADC_ZeroPage,
            [IRType.I8, IRType.I1],
            [.. adcUses]);

        builder.Function.ReplaceAllUsesOfRegister(resultVreg,   newDefs[0]);
        builder.Function.ReplaceAllUsesOfRegister(carryOutVreg, newDefs[1]);

        builder.Remove(instr);
        return true;
    }

    private static bool IsVRegDef(MachineOperand o) =>
        o is VirtualRegisterOperand v && v.IsDefinition;
}
