namespace DotNetro.Compiler.TypeSystem;

internal sealed class SZArrayType(TypeSystemContext context, TypeDescription elementType)
    : ParameterizedType(context, elementType)
{
    public override int InstanceSize => Context.PointerSize;

    public override string EncodedName => $"{ElementType.EncodedName}_SZArray";
}
