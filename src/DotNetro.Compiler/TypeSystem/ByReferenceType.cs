namespace DotNetro.Compiler.TypeSystem;

internal sealed class ByReferenceType(TypeSystem typeSystem, TypeDescription elementType)
    : ParameterizedType(elementType)
{
    public override int Size => typeSystem.PointerSize;

    public override string EncodedName => throw new NotImplementedException();
}