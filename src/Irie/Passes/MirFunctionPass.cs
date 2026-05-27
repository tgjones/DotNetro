using Irie.Mir;

namespace Irie.Passes;

public abstract class MirFunctionPass : Pass
{
    public sealed override void Run(CompilationContext context)
    {
        foreach (var function in context.Module.Functions)
            Run(function);
    }

    public abstract void Run(MirFunction function);

    protected TResult GetAnalysis<T, TResult>(MirFunction function) where T : MirFunctionAnalysis<TResult>, new()
        => (PassManager ?? throw new InvalidOperationException($"{Name}: PassManager not set."))
           .GetAnalysis<T, TResult>(function);
}
