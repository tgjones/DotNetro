using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;
using DotNetro.Compiler.Util;

namespace DotNetro.Compiler;

internal sealed class EcmaType : TypeDescription
{
    private readonly Dictionary<FieldDefinitionHandle, EcmaField> _fields = [];

    public EcmaAssembly Assembly { get; }

    public TypeDefinitionHandle TypeDefinitionHandle { get; }

    public TypeDefinition TypeDefinition { get; }

    public string FullName { get; }

    public override string EncodedName { get; }

    public override int Size { get; }

    public EcmaType(EcmaAssembly assembly, TypeDefinitionHandle typeDefinitionHandle)
    {
        Assembly = assembly;
        TypeDefinitionHandle = typeDefinitionHandle;

        TypeDefinition = Assembly.MetadataReader.GetTypeDefinition(TypeDefinitionHandle);

        FullName = TypeDefinition.GetFullName(assembly.MetadataReader);
        EncodedName = FullName.Replace('.', '_');

        BuildFields();

        Size = _fields.Sum(x => x.Value.Type.Size);
    }

    public EcmaMethod GetMethod(string name, MethodSignature<TypeDescription> signature)
    {
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            if (Assembly.MetadataReader.GetString(methodDefinition.Name) == name)
            {
                var methodSignature = methodDefinition.DecodeSignature(Assembly.AssemblyStore.SignatureTypeProvider, GenericContext.Empty);
                if (AreMethodSignaturesCompatible(signature, methodSignature))
                {
                    return Assembly.GetMethod(methodDefinitionHandle);
                }
            }
        }

        throw new InvalidOperationException($"Could not find method {name}");
    }

    private static bool AreMethodSignaturesCompatible(in MethodSignature<TypeDescription> a, in MethodSignature<TypeDescription> b)
    {
        if (a.ReturnType != b.ReturnType)
        {
            return false;
        }

        if (!a.Header.Equals(b.Header))
        {
            return false;
        }

        if (a.GenericParameterCount != b.GenericParameterCount)
        {
            return false;
        }

        if (a.RequiredParameterCount != b.RequiredParameterCount)
        {
            return false;
        }

        if (a.ParameterTypes.Length != b.ParameterTypes.Length)
        {
            return false;
        }

        for (var i = 0; i < a.ParameterTypes.Length; i++)
        {
            if (a.ParameterTypes[i] != b.ParameterTypes[i])
            {
                return false;
            }
        }

        return true;
    }

    private void BuildFields()
    {
        var offset = 0;

        foreach (var fieldDefinitionHandle in TypeDefinition.GetFields())
        {
            var fieldDefinition = Assembly.MetadataReader.GetFieldDefinition(fieldDefinitionHandle);

            if (fieldDefinition.Attributes.HasFlag(FieldAttributes.Static))
            {
                continue;
            }

            var fieldType = fieldDefinition.DecodeSignature(
                Assembly.AssemblyStore.SignatureTypeProvider,
                GenericContext.Empty);

            _fields.Add(fieldDefinitionHandle, new EcmaField(this, fieldDefinition, fieldType, offset));

            offset += fieldType.Size;
        }
    }

    public EcmaField GetField(FieldDefinitionHandle handle) => _fields[handle];
}
