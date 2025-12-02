namespace Irie.IR;

public sealed class IRUse(IRValue parent)
{
    public readonly IRValue Parent = parent;

    public required IRValue Value { get; set; }
}