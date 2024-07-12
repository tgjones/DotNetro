namespace DotNetro.Compiler.TypeSystem;

internal sealed record Instantiation(TypeDescription[] GenericParameters)
{
    public static readonly Instantiation Empty = new([]);
}