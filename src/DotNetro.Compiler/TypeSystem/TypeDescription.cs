namespace DotNetro.Compiler.TypeSystem;

internal abstract class TypeDescription(TypeSystemContext context)
{
    public TypeSystemContext Context { get; } = context;

    /// <summary>
    /// Size in bytes of the data represented by this type.
    /// </summary>
    public abstract int InstanceSize { get; }

    /// <summary>
    /// The size of the type when it's manipulated on the stack or stored in a field.
    /// For all-but-one types, this is the same as <see cref="InstanceSize"/>.
    /// But for <see cref="EcmaType"/>, specifically for non-value-types, it's
    /// the pointer size.
    /// </summary>
    public virtual int Size => InstanceSize;

    public virtual bool IsPointerLike { get; } = false;

    public abstract string EncodedName { get; }

    public ByReferenceType MakeByReferenceType() => Context.GetByReferenceType(this);

    public PointerType MakePointerType() => Context.GetPointerType(this);

    public SZArrayType MakeSZArrayType() => Context.GetSZArrayType(this);
}
