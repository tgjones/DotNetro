using System.Collections.Immutable;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaMethod : IEquatable<EcmaMethod>
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
            var localTypes = localSignature.DecodeLocalSignature(declaringType.Assembly.AssemblyStore.SignatureTypeProvider, GenericContext.Empty);
            var localOffset = 0;
            var localsBuilder = ImmutableArray.CreateBuilder<LocalVariable>(localTypes.Length);
            for (var i = 0; i < localTypes.Length; i++)
            {
                localsBuilder.Add(new LocalVariable(this, i, localOffset, localTypes[i]));
                localOffset += localTypes[i].Size;
            }
            LocalVariables = localsBuilder.ToImmutable();
            LocalsSize = localOffset;
        }
        else
        {
            LocalVariables = [];
        }

        MethodSignature = methodDefinition.DecodeSignature(declaringType.Assembly.AssemblyStore.SignatureTypeProvider, GenericContext.Empty);

        var parameterOffset = 0;
        var parametersBuilder = ImmutableArray.CreateBuilder<Parameter>(MethodSignature.ParameterTypes.Length);
        for (var i = 0; i < MethodSignature.ParameterTypes.Length; i++)
        {
            parametersBuilder.Add(new Parameter(i, parameterOffset, MethodSignature.ParameterTypes[i]));
            parameterOffset += MethodSignature.ParameterTypes[i].Size;
        }
        Parameters = parametersBuilder.ToImmutable();
        ParametersSize = parameterOffset;

        UniqueName = $"{DeclaringType.FullName.Replace('.', '_')}_{MetadataReader.GetString(methodDefinition.Name)}";

        foreach (var parameterType in MethodSignature.ParameterTypes)
        {
            UniqueName += $"_{parameterType.EncodedName}";
        }
    }

    public bool Equals(EcmaMethod other)
    {
        return DeclaringType.Equals(other.DeclaringType)
            && MethodDefinitionHandle.Equals(other.MethodDefinitionHandle);
    }
}
