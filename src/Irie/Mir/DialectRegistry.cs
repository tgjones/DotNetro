namespace Irie.Mir;

// Process-global table of registered dialects. IDs are dense ints assigned in
// registration order; the registry never references a dialect class by name.
public static class DialectRegistry
{
    private static readonly List<Dialect> _byId = [];
    private static readonly Dictionary<string, Dialect> _byPrefix = [];

    public static DialectId Register(Dialect dialect)
    {
        if (_byPrefix.ContainsKey(dialect.Prefix))
            throw new InvalidOperationException(
                $"A dialect with prefix '{dialect.Prefix}' is already registered.");

        var id = new DialectId(_byId.Count);
        _byId.Add(dialect);
        _byPrefix[dialect.Prefix] = dialect;
        dialect.OnRegistered(id);
        return id;
    }

    public static Dialect ById(DialectId id) => _byId[id.Index];

    public static Dialect ByPrefix(string prefix) => _byPrefix[prefix];

    public static bool TryByPrefix(string prefix, out Dialect dialect) =>
        _byPrefix.TryGetValue(prefix, out dialect!);
}
