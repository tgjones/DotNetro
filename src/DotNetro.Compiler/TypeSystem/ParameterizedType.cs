namespace DotNetro.Compiler.TypeSystem;

internal abstract class ParameterizedType(TypeDescription elementType)
    : TypeDescription
{
    public TypeDescription ElementType { get; } = elementType;
}
