using System.Collections.Concurrent;
using System.Dynamic;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class TypeSystem(int pointerSize)
{
    private readonly ConcurrentDictionary<TypeDescription, TypeDescription> _pointerTypes = new();
    private readonly ConcurrentDictionary<PrimitiveTypeCode, TypeDescription> _primitiveTypes = new();
    private readonly ConcurrentDictionary<TypeDescription, TypeDescription> _szArrayTypes = new();

    public int PointerSize { get; } = pointerSize;

    public TypeDescription Int32 => GetPrimitiveType(PrimitiveTypeCode.Int32);

    public TypeDescription String => GetPrimitiveType(PrimitiveTypeCode.String);

    public TypeDescription GetPointerType(TypeDescription elementType)
    {
        return _pointerTypes.GetOrAdd(elementType, x => new PointerType(this, x));
    }

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return _primitiveTypes.GetOrAdd(typeCode, x => new PrimitiveType(this, x));
    }

    public TypeDescription GetSZArrayType(TypeDescription elementType)
    {
        return _szArrayTypes.GetOrAdd(elementType, static x => new SZArrayType(x));
    }
}
