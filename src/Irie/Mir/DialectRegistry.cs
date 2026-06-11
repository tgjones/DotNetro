namespace Irie.Mir;

// Process-global table of registered dialects. IDs are dense ints assigned in
// registration order; the registry never references a dialect class by name.
//
// Thread-safe: target dialects register lazily from their target's
// constructor, so multiple threads can race to register the same prefix on
// first use. All access is guarded by _lock, and
// Register calls OnRegistered (which publishes the dialect's static Id) before
// the dialect becomes observable via _byPrefix/_byId — so no thread can ever
// see a dialect as "registered" while its static Id is still its default.
public static class DialectRegistry
{
    private static readonly List<Dialect> _byId = [];
    private static readonly Dictionary<string, Dialect> _byPrefix = [];
    private static readonly object _lock = new();

    public static DialectId Register(Dialect dialect)
    {
        lock (_lock)
        {
            if (_byPrefix.ContainsKey(dialect.Prefix))
                throw new InvalidOperationException(
                    $"A dialect with prefix '{dialect.Prefix}' is already registered.");

            return RegisterUnlocked(dialect);
        }
    }

    // Atomic check-and-register: returns the already-registered dialect for the
    // prefix, or registers the one produced by factory. Used by target ctors,
    // which can race to register their dialect on first construction.
    public static Dialect GetOrRegister(string prefix, Func<Dialect> factory)
    {
        lock (_lock)
        {
            if (_byPrefix.TryGetValue(prefix, out var existing))
                return existing;

            var dialect = factory();
            if (dialect.Prefix != prefix)
                throw new InvalidOperationException(
                    $"GetOrRegister asked for prefix '{prefix}' but factory produced a dialect with prefix '{dialect.Prefix}'.");

            RegisterUnlocked(dialect);
            return dialect;
        }
    }

    // Caller must hold _lock. Publishes the static Id (via OnRegistered) before
    // adding to the lookup tables so the dialect is never visible as registered
    // with a stale Id.
    private static DialectId RegisterUnlocked(Dialect dialect)
    {
        var id = new DialectId(_byId.Count);
        dialect.Id = id;
        dialect.OnRegistered(id);
        _byId.Add(dialect);
        _byPrefix[dialect.Prefix] = dialect;
        return id;
    }

    public static Dialect ById(DialectId id)
    {
        lock (_lock)
            return _byId[id.Index];
    }

    public static Dialect ByPrefix(string prefix)
    {
        lock (_lock)
            return _byPrefix[prefix];
    }

    public static bool TryByPrefix(string prefix, out Dialect dialect)
    {
        lock (_lock)
            return _byPrefix.TryGetValue(prefix, out dialect!);
    }
}
