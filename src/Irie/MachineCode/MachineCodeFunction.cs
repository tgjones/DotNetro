namespace Irie.MachineCode;

public sealed class MachineCodeFunction(string name)
{
    public string Name => name;
    public List<MachineCodeEntry> Body { get; } = [];

    public MachineCodeLabel EmitLabel(string labelName)
    {
        var label = new MachineCodeLabel(labelName);
        Body.Add(label);
        return label;
    }

    public MachineCodeInstruction EmitInstruction(int opcode, params MachineCodeOperand[] operands)
    {
        var instruction = new MachineCodeInstruction(opcode, operands);
        Body.Add(instruction);
        return instruction;
    }
}
