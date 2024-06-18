using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class LocalVariable(EcmaMethod parent, int index, int offset, TypeDescription type)
{
    public EcmaMethod Parent { get; } = parent;

    public int Index { get; } = index;

    public int Offset { get; } = offset;

    public TypeDescription Type { get; } = type;
}
