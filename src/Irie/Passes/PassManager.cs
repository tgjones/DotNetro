using Irie.Mir;

namespace Irie.Passes;

public sealed class PassManager(string? stopAfterPass = null, string? startAtPass = null)
{
    private readonly List<Pass> _passes = [];
    private bool _stopped;
    private bool _started = startAtPass == null;

    public void AddPass(Pass pass)
    {
        if (_stopped) return;

        if (!_started)
        {
            if (string.Equals(pass.Name, startAtPass, StringComparison.InvariantCultureIgnoreCase))
                _started = true;
            else
                return;
        }

        pass.PassManager = this;
        _passes.Add(pass);

        if (string.Equals(pass.Name, stopAfterPass, StringComparison.InvariantCultureIgnoreCase))
            _stopped = true;
    }

    public void Run(CompilationContext context)
    {
        foreach (var pass in _passes)
            pass.Run(context);
    }

    public TResult GetAnalysis<T, TResult>(MirFunction function) where T : MirFunctionAnalysis<TResult>, new()
    {
        // TODO: Cache analysis results to avoid redundant computation if the same analysis is requested
        // multiple times for the same function. But then we'll need each pass to declare whether it
        // invalidates / preserves the analysis.
        var analysis = new T();
        return analysis.Compute(function);
    }
}
