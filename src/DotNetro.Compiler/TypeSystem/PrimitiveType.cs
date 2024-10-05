using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class PrimitiveType(TypeSystemContext context, PrimitiveTypeCode primitiveTypeCode)
    : TypeDescription(context)
{
    public PrimitiveTypeCode PrimitiveTypeCode { get; } = primitiveTypeCode;

    public override int InstanceSize { get; } = primitiveTypeCode switch
    {
        PrimitiveTypeCode.Boolean => 1,
        PrimitiveTypeCode.Byte => 1,
        PrimitiveTypeCode.Char => 1,
        PrimitiveTypeCode.Double => 8,
        PrimitiveTypeCode.Int32 => 4,
        PrimitiveTypeCode.Int64 => 8,
        PrimitiveTypeCode.IntPtr => context.PointerSize,
        PrimitiveTypeCode.Object => context.PointerSize,
        PrimitiveTypeCode.Single => 4,
        PrimitiveTypeCode.String => context.PointerSize,
        PrimitiveTypeCode.UInt16 => 2,
        PrimitiveTypeCode.UInt32 => 4,
        PrimitiveTypeCode.UInt64 => 8,
        PrimitiveTypeCode.UIntPtr => context.PointerSize,
        PrimitiveTypeCode.Void => 0,
        _ => throw new InvalidOperationException(),
    };

    public override bool IsPointerLike { get; } = primitiveTypeCode == PrimitiveTypeCode.IntPtr;

    public override string EncodedName => PrimitiveTypeCode.ToString();
}
