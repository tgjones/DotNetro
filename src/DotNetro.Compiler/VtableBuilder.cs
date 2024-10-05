using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetro.Compiler;

internal sealed class VtableTracker
{
    private readonly HashSet<EcmaMethod> _usedMethods = [];
    private readonly HashSet<EcmaType> _usedTypes = [];

    public string GetVtableSlotLabel(EcmaMethod method)
    {
        _usedMethods.Add(method);

        return VtableSlot.GetLabel(method);
    }

    public void MarkTypeUsed(EcmaType type)
    {
        _usedTypes.Add(type);
    }

    public Vtable[] BuildVtables()
    {
        var builder = new VtableBuilder(_usedMethods);
        var result = new List<Vtable>();

        foreach (var type in _usedTypes)
        {
            result.Add(builder.GetVtable(type));
        }

        return [.. result];
    }
}

internal sealed class VtableBuilder
{
    private readonly ConcurrentDictionary<EcmaType, Vtable> _vtables = new();
    private readonly HashSet<EcmaMethod> _usedMethods;

    public VtableBuilder(HashSet<EcmaMethod> usedMethods)
    {
        _usedMethods = usedMethods;
    }

    public Vtable GetVtable(EcmaType type)
    {
        return _vtables.GetOrAdd(type, Build);
    }

    private Vtable Build(EcmaType type)
    {
        // Ensure base vtable is built.
        var baseVtable = type.BaseType != null
            ? GetVtable(type.BaseType)
            : null;

        // Start with vtable inherited from base type.
        var vtableSlots = new List<VtableSlot>(baseVtable?.Slots ?? []);

        var slotIndex = (byte)0;

        // Loop over all methods.
        foreach (var methodDefinitionHandle in type.TypeDefinition.GetMethods())
        {
            var methodDefinition = type.Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            var method = type.Assembly.GetMethod(methodDefinitionHandle);

            // Is this a virtual method?
            if (methodDefinition.Attributes.HasFlag(MethodAttributes.Virtual))
            {
                if (methodDefinition.Attributes.HasFlag(MethodAttributes.NewSlot))
                {
                    // Was it actually called?
                    if (_usedMethods.Contains(method))
                    {
                        // Add new slot.
                        vtableSlots.Add(new VtableSlot(slotIndex++, method, method));
                    }
                }
                else
                {
                    // Find method that this method overrides.
                    var overriddenMethod = FindOverriddenMethod(method);

                    // Was it actually called?
                    if (_usedMethods.Contains(overriddenMethod))
                    {
                        // Replace overridden method at correct slot.
                        vtableSlots[GetVtable(overriddenMethod.DeclaringType).GetSlot(overriddenMethod).Index] = new VtableSlot(slotIndex++, method, overriddenMethod);
                    }
                }
            }
        }

        return new Vtable(type, [.. vtableSlots]);
    }

    private static EcmaMethod FindOverriddenMethod(EcmaMethod method)
    {
        var baseType = method.DeclaringType.BaseType;

        while (baseType != null)
        {
            if (baseType.TryGetMethod(method.Name, method.MethodSignature, out var overriddenMethod))
            {
                return overriddenMethod;
            }

            baseType = baseType.BaseType;
        }

        throw new InvalidOperationException();
    }
}

internal sealed record Vtable(EcmaType Type, VtableSlot[] Slots)
{
    public static string GetName(EcmaType type) => $"Vtable_{type.EncodedName}";

    public string Name { get; } = GetName(Type);

    public VtableSlot GetSlot(EcmaMethod method) => Slots.Single(x => x.Method == method);
}

internal sealed record VtableSlot(byte Index, EcmaMethod Method, EcmaMethod OverriddenMethod)
{
    public string Label { get; } = GetLabel(OverriddenMethod);

    public static string GetLabel(EcmaMethod method) => $"VtableSlot_{method.UniqueName}";
}