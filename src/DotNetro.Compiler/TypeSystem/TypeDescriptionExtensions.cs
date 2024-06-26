namespace DotNetro.Compiler.TypeSystem;

internal static class TypeDescriptionExtensions
{
    public static bool IsWellKnownType(this TypeDescription type, WellKnownType wellKnownType)
    {
        return type == type.Context.GetWellKnownType(wellKnownType);
    }
}
