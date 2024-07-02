using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaMethod
{
    public EcmaType DeclaringType { get; }

    public MethodDefinitionHandle MethodDefinitionHandle { get; }

    public MethodBodyBlock MethodBody { get; }

    public ImmutableArray<Parameter> Parameters { get; }

    public int ParametersSize { get; }

    public ImmutableArray<LocalVariable> LocalVariables { get; }

    public int LocalsSize { get; }

    public MethodSignature<TypeDescription> MethodSignature { get; }

    public MetadataReader MetadataReader { get; }

    public string UniqueName { get; }

    public EcmaMethod(
        EcmaType declaringType,
        MethodDefinitionHandle handle)
    {
        DeclaringType = declaringType;
        MethodDefinitionHandle = handle;

        MetadataReader = declaringType.Assembly.MetadataReader;

        var methodDefinition = MetadataReader.GetMethodDefinition(handle);

        MethodBody = declaringType.Assembly.PEReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);

        if (!MethodBody.LocalSignature.IsNil)
        {
            var localSignature = MetadataReader.GetStandaloneSignature(MethodBody.LocalSignature);
            var localTypes = localSignature.DecodeLocalSignature(declaringType.Assembly.SignatureTypeProvider, GenericContext.Empty);
            var localOffset = 0;
            var localsBuilder = ImmutableArray.CreateBuilder<LocalVariable>(localTypes.Length);
            for (var i = 0; i < localTypes.Length; i++)
            {
                var local = new LocalVariable(this, i, localOffset, localTypes[i]);
                localsBuilder.Add(local);
                localOffset += local.Type.Size;
            }
            LocalVariables = localsBuilder.ToImmutable();
            LocalsSize = localOffset;
        }
        else
        {
            LocalVariables = [];
        }

        MethodSignature = methodDefinition.DecodeSignature(declaringType.Assembly.SignatureTypeProvider, GenericContext.Empty);

        var parameterOffset = 0;
        var parametersBuilder = ImmutableArray.CreateBuilder<Parameter>(MethodSignature.ParameterTypes.Length);
        var parameterIndex = 0;
        if (!methodDefinition.Attributes.HasFlag(MethodAttributes.Static))
        {
            TypeDescription parameterType = declaringType.IsValueType
                ? declaringType.MakeByReferenceType()
                : declaringType;

            var parameter = new Parameter(parameterIndex++, parameterOffset, parameterType);
            parametersBuilder.Add(parameter);
            parameterOffset += parameter.Type.Size;
        }
        for (var i = 0; i < MethodSignature.ParameterTypes.Length; i++)
        {
            var parameter = new Parameter(parameterIndex++, parameterOffset, MethodSignature.ParameterTypes[i]);
            parametersBuilder.Add(parameter);
            parameterOffset += parameter.Type.Size;
        }
        Parameters = parametersBuilder.ToImmutable();
        ParametersSize = parameterOffset;

        UniqueName = $"{DeclaringType.FullName.Replace('.', '_')}_{MetadataReader.GetString(methodDefinition.Name).Replace('.', '_')}";

        foreach (var parameterType in MethodSignature.ParameterTypes)
        {
            UniqueName += $"_{parameterType.EncodedName}";
        }
    }
}
