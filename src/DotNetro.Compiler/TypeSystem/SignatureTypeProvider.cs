using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class SignatureTypeProvider(TypeSystem typeSystem, AssemblyStore assemblyStore)
    : ISignatureTypeProvider<TypeDescription, GenericContext>
{
    public TypeDescription GetArrayType(TypeDescription elementType, ArrayShape shape)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetByReferenceType(TypeDescription elementType)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetFunctionPointerType(MethodSignature<TypeDescription> signature)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetGenericInstantiation(TypeDescription genericType, ImmutableArray<TypeDescription> typeArguments)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetModifiedType(TypeDescription modifier, TypeDescription unmodifiedType, bool isRequired)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetPinnedType(TypeDescription elementType)
    {
        throw new NotImplementedException();
    }

    public TypeDescription GetPointerType(TypeDescription elementType) => typeSystem.GetPointerType(elementType);

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode) => typeSystem.GetPrimitiveType(typeCode);

    public TypeDescription GetSZArrayType(TypeDescription elementType) => typeSystem.GetSZArrayType(elementType);

    public TypeDescription GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var assembly = assemblyStore.GetAssembly(reader.GetAssemblyDefinition().GetAssemblyName());
        return assembly.GetType(handle);
    }

    public TypeDescription GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var assembly = assemblyStore.GetAssembly(reader.GetAssemblyDefinition().GetAssemblyName());
        return assembly.ResolveType(handle);
    }

    public TypeDescription GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        throw new NotImplementedException();
    }
}