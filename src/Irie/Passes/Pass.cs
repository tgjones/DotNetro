namespace Irie.Passes;

public abstract class Pass
{
    public abstract string Name { get; }
    public abstract void Run(CompilationContext context);

    internal PassManager? PassManager { get; set; }
}
