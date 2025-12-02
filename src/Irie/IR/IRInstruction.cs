namespace Irie.IR;

public abstract class IRInstruction : IRValue
{
    public bool HasResult => Type is not VoidType;

    public bool HasSideEffects => Type is VoidType;

    public IRBasicBlock? Parent { get; set; }

    public readonly IRUse[] Operands;

    public IRInstruction(IRType type, IRValue[] operands)
        : base(type)
    {
        Operands = new IRUse[operands.Length];

        for (var i = 0; i < operands.Length; i++)
        {
            var operand = operands[i];

            var use = new IRUse(this)
            {
                Value = operand
            };

            Operands[i] = use;

            operand.Uses.Add(use);
        }
    }

    public void RemoveFromParent()
    {
        foreach (var operand in Operands)
        {
            operand.Value.Uses.Remove(operand);
        }
        Parent!.Instructions.Remove(this);
        Parent = null;
    }
}
