using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;

namespace DotNetro.Compiler.TypeSystem;

internal sealed class TypeSystemContext(int pointerSize) : IDisposable
{
    private readonly Dictionary<string, EcmaAssembly> _assembliesByPath = [];
    private readonly Dictionary<string, EcmaAssembly> _assembliesByName = [];

    private readonly ConcurrentDictionary<TypeDescription, ByReferenceType> _byReferenceTypes = new();
    private readonly ConcurrentDictionary<TypeDescription, PointerType> _pointerTypes = new();
    private readonly ConcurrentDictionary<PrimitiveTypeCode, TypeDescription> _primitiveTypes = new();
    private readonly ConcurrentDictionary<TypeDescription, SZArrayType> _szArrayTypes = new();
    private readonly ConcurrentDictionary<WellKnownType, TypeDescription> _wellKnownTypes = new();
    private readonly ConcurrentDictionary<(Instantiation, int), GenericParameter> _genericMethodParameters = new();
    private readonly ConcurrentDictionary<(Instantiation, int), GenericParameter> _genericTypeParameters = new();
    private readonly ConcurrentDictionary<(TypeDescription, Instantiation), InstantiatedType> _instantiatedTypes = new();

    public EcmaAssembly CoreAssembly => ResolveAssembly(new AssemblyName("mscorlib"));

    public EcmaAssembly RuntimeAssembly => ResolveAssembly(new AssemblyName("DotNetro"));

    public int PointerSize { get; } = pointerSize;

    public TypeDescription Boolean => GetPrimitiveType(PrimitiveTypeCode.Boolean);

    public TypeDescription Int32 => GetPrimitiveType(PrimitiveTypeCode.Int32);

    public TypeDescription IntPtr => GetPrimitiveType(PrimitiveTypeCode.IntPtr);

    public TypeDescription String => GetPrimitiveType(PrimitiveTypeCode.String);

    public ByReferenceType GetByReferenceType(TypeDescription elementType)
    {
        return _byReferenceTypes.GetOrAdd(elementType, x => new ByReferenceType(this, x));
    }

    public InstantiatedType GetInstantiatedType(TypeDescription genericType, Instantiation instantiation)
    {
        return _instantiatedTypes.GetOrAdd((genericType, instantiation), x => new InstantiatedType(this, (EcmaType)genericType, instantiation));
    }

    public PointerType GetPointerType(TypeDescription elementType)
    {
        return _pointerTypes.GetOrAdd(elementType, x => new PointerType(this, x));
    }

    public TypeDescription GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return _primitiveTypes.GetOrAdd(typeCode, x => new PrimitiveType(this, x));
    }

    public SZArrayType GetSZArrayType(TypeDescription elementType)
    {
        return _szArrayTypes.GetOrAdd(elementType, x => new SZArrayType(this, x));
    }

    public TypeDescription GetWellKnownType(WellKnownType wellKnownType)
    {
        return _wellKnownTypes.GetOrAdd(wellKnownType, x => CoreAssembly.GetType("System", wellKnownType.ToString()));
    }

    public GenericParameter GetGenericMethodParameter(Instantiation context, int index)
    {
        return _genericMethodParameters.GetOrAdd((context, index), x => new GenericParameter(this, GenericParameterKind.Method, index));
    }

    public GenericParameter GetGenericTypeParameter(Instantiation context, int index)
    {
        return _genericTypeParameters.GetOrAdd((context, index), x => new GenericParameter(this, GenericParameterKind.Type, index));
    }

    public EcmaAssembly ResolveAssembly(string assemblyPath)
    {
        if (!_assembliesByPath.TryGetValue(assemblyPath, out var assemblyContext))
        {
            assemblyContext = new EcmaAssembly(this, assemblyPath);
            _assembliesByPath.Add(assemblyPath, assemblyContext);
        }

        return assemblyContext;
    }

    public EcmaAssembly ResolveAssembly(AssemblyName assemblyName)
    {
        var fullName = assemblyName.FullName;

        if (!_assembliesByName.TryGetValue(fullName, out var result))
        {
            var assembly = Assembly.Load(fullName);
            result = ResolveAssembly(assembly.Location);
            _assembliesByName.Add(fullName, result);
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var assembly in _assembliesByPath.Values)
        {
            assembly.Dispose();
        }
    }
}
