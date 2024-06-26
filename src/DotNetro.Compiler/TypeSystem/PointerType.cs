namespace DotNetro.Compiler.TypeSystem;

internal sealed class PointerType(TypeSystemContext context, TypeDescription elementType)
    : ParameterizedType(context, elementType)
{
    public override int InstanceSize => Context.PointerSize;

    public override bool IsPointerLike { get; } = true;

    public override string EncodedName => throw new NotImplementedException();
}
