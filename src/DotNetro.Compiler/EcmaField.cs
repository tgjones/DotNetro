using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class EcmaField(EcmaType owner, FieldDefinition fieldDefinition, TypeDescription type, int offset)
{
    public EcmaType Owner { get; } = owner;

    public FieldDefinition Definition { get; } = fieldDefinition;

    public TypeDescription Type { get; } = type;

    public int Offset { get; } = offset;

    public string Name { get; } = owner.Assembly.MetadataReader.GetString(fieldDefinition.Name);
}
