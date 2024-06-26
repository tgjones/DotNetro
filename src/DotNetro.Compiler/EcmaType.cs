using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;
using DotNetro.Compiler.Util;

namespace DotNetro.Compiler;

internal sealed class EcmaType : TypeDescription
{
    private readonly Dictionary<FieldDefinitionHandle, EcmaField> _fields = [];
    private bool _builtFields;
    private int _size;
    private bool _builtBaseType;
    private EcmaType? _baseType;
    private bool _builtIsValueType;
    private bool _isValueType;

    public EcmaAssembly Assembly { get; }

    public TypeDefinitionHandle TypeDefinitionHandle { get; }

    public TypeDefinition TypeDefinition { get; }

    public string FullName { get; }

    public override string EncodedName { get; }

    public override int InstanceSize
    {
        get
        {
            EnsureFields();
            return _size;
        }
    }

    public override int Size => !IsValueType
        ? Context.PointerSize
        : InstanceSize;

    public override bool IsPointerLike => !IsValueType;

    public EcmaType? BaseType
    {
        get
        {
            EnsureBaseType();
            return _baseType;
        }
    }

    public bool IsValueType
    {
        get
        {
            EnsureIsValueType();
            return _isValueType;
        }
    }

    public EcmaType(EcmaAssembly assembly, TypeDefinitionHandle typeDefinitionHandle)
        : base(assembly.Context)
    {
        Assembly = assembly;
        TypeDefinitionHandle = typeDefinitionHandle;

        TypeDefinition = Assembly.MetadataReader.GetTypeDefinition(TypeDefinitionHandle);

        FullName = TypeDefinition.GetFullName(assembly.MetadataReader);
        EncodedName = FullName.Replace('.', '_');

        
    }

    public EcmaMethod? GetStaticConstructor()
    {
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            if (Assembly.MetadataReader.GetString(methodDefinition.Name) == ".cctor")
            {
                return Assembly.GetMethod(methodDefinitionHandle);
            }
        }

        return null;
    }

    public EcmaMethod GetMethod(string name, MethodSignature<TypeDescription> signature)
    {
        foreach (var methodDefinitionHandle in TypeDefinition.GetMethods())
        {
            var methodDefinition = Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            if (Assembly.MetadataReader.GetString(methodDefinition.Name) == name)
            {
                var methodSignature = methodDefinition.DecodeSignature(Assembly.SignatureTypeProvider, GenericContext.Empty);
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

    private void EnsureFields()
    {
        if (_builtFields)
        {
            return;
        }

        var offset = 0;

        foreach (var fieldDefinitionHandle in TypeDefinition.GetFields())
        {
            var fieldDefinition = Assembly.MetadataReader.GetFieldDefinition(fieldDefinitionHandle);

            var fieldType = fieldDefinition.DecodeSignature(
                Assembly.SignatureTypeProvider,
                GenericContext.Empty);

            var isStaticField = fieldDefinition.Attributes.HasFlag(FieldAttributes.Static);

            _fields.Add(fieldDefinitionHandle, new EcmaField(this, fieldDefinition, fieldType, isStaticField ? 0 : offset));

            if (!isStaticField)
            {
                offset += fieldType.Size;
            }
        }

        _size = offset;

        _builtFields = true;
    }

    public EcmaField GetField(FieldDefinitionHandle handle)
    {
        EnsureFields();
        return _fields[handle];
    }

    private void EnsureBaseType()
    {
        if (_builtBaseType)
        {
            return;
        }

        _baseType = !TypeDefinition.BaseType.IsNil
            ? Assembly.ResolveType(TypeDefinition.BaseType)
            : null;

        _builtBaseType = true;
    }

    private void EnsureIsValueType()
    {
        if (_builtIsValueType)
        {
            return;
        }

        _isValueType = BaseType?.IsWellKnownType(WellKnownType.ValueType) ?? false;

        _builtIsValueType = true;
    }
}
