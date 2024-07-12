using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class SignatureTypeProvider(EcmaAssembly assembly)
    : ISignatureTypeProvider<TypeDescription, Instantiation>
{
    public TypeDescription GetArrayType(TypeDescription elementType, ArrayShape shape)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetByReferenceType(TypeDescription elementType) => elementType.MakeByReferenceType();

    public TypeDescription GetFunctionPointerType(MethodSignature<TypeDescription> signature)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetGenericInstantiation(TypeDescription genericType, ImmutableArray<TypeDescription> typeArguments)
    {
        return genericType.MakeInstantiatedType(new Instantiation([.. typeArguments]));
    }

    public TypeDescription GetGenericMethodParameter(Instantiation genericContext, int index)
    {
        return assembly.Context.GetGenericMethodParameter(genericContext, index);
    }

    public TypeDescription GetGenericTypeParameter(Instantiation genericContext, int index)
    {
        return assembly.Context.GetGenericTypeParameter(genericContext, index);
    }

    public TypeDescription GetModifiedType(TypeDescription modifier, TypeDescription unmodifiedType, bool isRequired)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetPinnedType(TypeDescription elementType)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetPointerType(TypeDescription elementType) => elementType.MakePointerType();

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode) => assembly.Context.GetPrimitiveType(typeCode);

    public TypeDescription GetSZArrayType(TypeDescription elementType) => elementType.MakeSZArrayType();

    public TypeDescription GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        return assembly.GetType(handle);
    }

    public TypeDescription GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        return assembly.ResolveType(handle);
    }

    public TypeDescription GetTypeFromSpecification(MetadataReader reader, Instantiation genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        Debug.Assert(reader == assembly.MetadataReader);
        throw new NotImplementedException();
    }
}