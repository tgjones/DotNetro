namespace DotNetro.Compiler.TypeSystem;

internal sealed class SZArrayType(TypeSystemContext context, TypeDescription elementType)
    : ParameterizedType(context, elementType)
{
    public override int InstanceSize => throw new NotImplementedException();

    public override string EncodedName => throw new NotImplementedException();
}
