namespace DotNetro.Compiler.TypeSystem;

internal abstract class TypeDescription
{
    public abstract int Size { get; }

    public abstract string EncodedName { get; }
}
