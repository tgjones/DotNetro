namespace Irie.CodeGen.Passes;

public sealed class MachineFunctionPassManager
{
    private readonly List<MachineFunctionPass> _passes = [];

    public void AddPass(MachineFunctionPass pass) => _passes.Add(pass);

    public void Run(MachineModule module)
    {
        foreach (var function in module.Functions)
            Run(function);
    }

    public void Run(MachineFunction function)
    {
        foreach (var pass in _passes)
            pass.RunOnFunction(function);
    }
}
