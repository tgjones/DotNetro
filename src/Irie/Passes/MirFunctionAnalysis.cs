using Irie.Mir;

namespace Irie.Passes;

// Base class for analyses that compute information about a MirFunction.
// Analyses are not passes — they are never added to a PassManager pipeline.
// To run an analysis, call PassManager.GetAnalysis<T, TResult>(function).
public abstract class MirFunctionAnalysis<TResult>
{
    public abstract TResult Compute(MirFunction function);
}
