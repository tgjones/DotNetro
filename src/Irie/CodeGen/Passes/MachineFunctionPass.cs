namespace Irie.CodeGen.Passes;

public abstract class MachineFunctionPass
{
    public abstract string Name { get; }

    public abstract void RunOnFunction(MachineFunction function);
}
