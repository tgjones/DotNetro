using System.Reflection;

using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

internal sealed class AssemblyStore : IDisposable
{
    private readonly Dictionary<string, EcmaAssembly> _assemblies = [];
    private readonly Dictionary<string, EcmaAssembly> _assembliesByName = [];

    public SignatureTypeProvider SignatureTypeProvider { get; }

    public AssemblyStore(TypeSystem.TypeSystem typeSystem)
    {
        SignatureTypeProvider = new SignatureTypeProvider(typeSystem, this);
    }

    public EcmaAssembly GetAssembly(string assemblyPath)
    {
        if (!_assemblies.TryGetValue(assemblyPath, out var assemblyContext))
        {
            assemblyContext = new EcmaAssembly(this, assemblyPath);
            _assemblies.Add(assemblyPath, assemblyContext);
        }

        return assemblyContext;
    }

    public EcmaAssembly GetAssembly(AssemblyName assemblyName)
    {
        var fullName = assemblyName.FullName;

        if (!_assembliesByName.TryGetValue(fullName, out var result))
        {
            var assembly = Assembly.Load(fullName);
            result = GetAssembly(assembly.Location);
            _assembliesByName.Add(fullName, result);
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values)
        {
            assembly.Dispose();
        }
    }
}
