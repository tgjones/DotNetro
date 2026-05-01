using Irie.CodeGen;
using Irie.IR;

namespace Irie;

public abstract class Pass
{
    public abstract string Name { get; }
    public abstract void Run(CompilationContext context);
}

public abstract class IRModulePass : Pass
{
    public sealed override void Run(CompilationContext context) => Run(context.IRModule);

    public abstract void Run(IRModule module);
}

public abstract class MachineFunctionPass : Pass
{
    public sealed override void Run(CompilationContext context)
    {
        foreach (var function in context.MachineModule.Functions)
            Run(function);
    }

    public abstract void Run(MachineFunction function);
}

public sealed class CompilationContext(IRModule irModule)
{
    private MachineModule? _machineModule;

    public IRModule IRModule { get; } = irModule;

    public MachineModule MachineModule => _machineModule ??= new MachineModule();
}

public sealed class PassManager(string? stopAfterPass = null)
{
    private readonly List<Pass> _passes = [];
    private bool _stopped;

    public void AddPass(Pass pass)
    {
        if (_stopped) return;

        _passes.Add(pass);

        if (string.Equals(pass.Name, stopAfterPass, StringComparison.InvariantCultureIgnoreCase))
            _stopped = true;
    }

    public void Run(CompilationContext context)
    {
        foreach (var pass in _passes)
            pass.Run(context);
    }
}
