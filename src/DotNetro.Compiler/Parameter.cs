using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class Parameter(int index, int offset, TypeDescription type)
{
    public int Index { get; } = index;

    public int Offset { get; } = offset;

    public TypeDescription Type { get; } = type;
}
