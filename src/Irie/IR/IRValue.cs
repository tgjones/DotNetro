namespace Irie.IR;

public abstract class IRValue(IRType type)
{
    public readonly IRType Type = type;

    public readonly List<IRUse> Uses = [];

    public void ReplaceAllUsesWith(IRValue newValue)
    {
        foreach (var use in Uses)
        {
            use.Value = newValue;
        }

        newValue.Uses.AddRange(Uses);

        Uses.Clear();
    }
}