namespace DotNetro.Compiler.TypeSystem;

internal sealed class InstantiatedType(TypeSystemContext context, EcmaType type, Instantiation instantiation)
    : TypeDescription(context)
{
    public override int InstanceSize => throw new NotImplementedException();

    public override string EncodedName => throw new NotImplementedException();

    public EcmaType Type { get; } = type;

    public Instantiation Instantiation { get; } = instantiation;
}
