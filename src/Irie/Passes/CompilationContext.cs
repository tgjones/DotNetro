using Irie.Mir;

namespace Irie.Passes;

public sealed class CompilationContext(MirModule module)
{
    public MirModule Module { get; } = module;
}
