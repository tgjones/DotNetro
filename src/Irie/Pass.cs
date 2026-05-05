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

public sealed class CompilationContext
{
    private MachineModule? _machineModule;
    private readonly TargetMIRInfo _target;

    public CompilationContext(IRModule irModule, TargetMIRInfo target)
    {
        IRModule = irModule;
        _target = target;
    }

    // For mid-pipeline entry (pre-built MIR input, no IR needed).
    public CompilationContext(MachineModule machineModule)
    {
        IRModule = new IRModule();
        _machineModule = machineModule;
        _target = machineModule.Target;
    }

    public IRModule IRModule { get; }

    public MachineModule MachineModule => _machineModule ??= new MachineModule(_target);
}

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
