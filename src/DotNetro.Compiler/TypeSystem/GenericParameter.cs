namespace DotNetro.Compiler.TypeSystem;

internal sealed class GenericParameter(TypeSystemContext context, GenericParameterKind kind, int index)
    : TypeDescription(context)
{
    public override int InstanceSize => throw new NotImplementedException();

    public override string EncodedName => throw new NotImplementedException();

    public GenericParameterKind Kind { get; } = kind;

    public int Index { get; } = index;
}

internal enum GenericParameterKind
{
    Type,
    Method,
}