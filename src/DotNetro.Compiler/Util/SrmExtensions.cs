using System.Reflection.Metadata;

namespace DotNetro.Compiler.Util;

internal static class SrmExtensions
{
    public static string GetFullName(this in TypeDefinition typeDefinition, MetadataReader metadataReader)
    {
        var result = "";

        if (!typeDefinition.Namespace.IsNil)
        {
            result = $"{metadataReader.GetString(typeDefinition.Namespace)}.";
        }

        result += metadataReader.GetString(typeDefinition.Name);

        return result;
    }
}
