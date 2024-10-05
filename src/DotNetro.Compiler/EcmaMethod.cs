using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaMethodBody
{
    public MethodBodyBlock? MethodBodyBlock { get; }

    public ImmutableArray<LocalVariable> LocalVariables { get; }

    public int LocalsSize { get; }

    public EcmaMethodBody(EcmaType declaringType, EcmaMethod method)
    {
        MethodBodyBlock = declaringType.Assembly.PEReader.GetMethodBody(method.MethodDefinition.RelativeVirtualAddress);

        if (!MethodBodyBlock.LocalSignature.IsNil)
        {
            var localSignature = method.MetadataReader.GetStandaloneSignature(MethodBodyBlock.LocalSignature);
            var localTypes = localSignature.DecodeLocalSignature(declaringType.Assembly.SignatureTypeProvider, Instantiation.Empty);
            var localOffset = 0;
            var localsBuilder = ImmutableArray.CreateBuilder<LocalVariable>(localTypes.Length);
            for (var i = 0; i < localTypes.Length; i++)
            {
                var local = new LocalVariable(method, i, localOffset, localTypes[i]);
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
    }
}

internal sealed class EcmaMethod
{
    public EcmaType DeclaringType { get; }

    public MethodDefinition MethodDefinition { get; }

    public ImmutableArray<Parameter> Parameters { get; }

    public int ParametersSize { get; }

    public MethodSignature<TypeDescription> MethodSignature { get; }

    public EcmaMethodBody? MethodBody { get; }

    public MetadataReader MetadataReader { get; }

    public string Name { get; }
    public string UniqueName { get; }

    public bool IsVirtual => MethodDefinition.Attributes.HasFlag(MethodAttributes.Virtual);

    public int FrameSize { get; }

    public EcmaMethod(EcmaType declaringType, in MethodDefinition methodDefinition)
    {
        DeclaringType = declaringType;

        MetadataReader = declaringType.Assembly.MetadataReader;

        MethodDefinition = methodDefinition;

        if (MethodDefinition.RelativeVirtualAddress != 0)
        {
            MethodBody = new EcmaMethodBody(declaringType, this);
        }

        MethodSignature = MethodDefinition.DecodeSignature(declaringType.Assembly.SignatureTypeProvider, Instantiation.Empty);

        var parameterOffset = 0;
        var parametersBuilder = ImmutableArray.CreateBuilder<Parameter>(MethodSignature.ParameterTypes.Length);
        var parameterIndex = 0;
        if (!MethodDefinition.Attributes.HasFlag(MethodAttributes.Static))
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

        Name = MetadataReader.GetString(MethodDefinition.Name);
        UniqueName = $"{DeclaringType.FullName.Replace('.', '_')}_{Name.Replace('.', '_')}";

        foreach (var parameterType in MethodSignature.ParameterTypes)
        {
            UniqueName += $"_{parameterType.EncodedName}";
        }

        FrameSize = ParametersSize + MethodBody?.LocalsSize ?? 0;
    }
}
