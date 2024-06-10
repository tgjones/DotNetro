namespace DotNetro.Compiler.TypeSystem;

internal sealed class SZArrayType(TypeDescription elementType)
    : ParameterizedType(elementType)
{
    public override int Size => throw new NotImplementedException();

    public override string EncodedName => throw new NotImplementedException();
}
