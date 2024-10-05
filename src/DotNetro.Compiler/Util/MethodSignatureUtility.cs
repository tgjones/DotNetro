using System.Reflection.Metadata;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler.Util;

internal static class MethodSignatureUtility
{
    public static bool AreCompatible(in MethodSignature<TypeDescription> a, in MethodSignature<TypeDescription> b)
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
}
