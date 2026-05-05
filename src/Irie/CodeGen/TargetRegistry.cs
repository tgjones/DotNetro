namespace Irie.CodeGen;

public static class TargetRegistry
{
    private static readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string key, Target target) => _targets[key] = target;

    public static Target Get(string key) =>
        _targets.TryGetValue(key, out var target) ? target
        : throw new ArgumentException(
            $"Unknown target '{key}'. Known targets: {string.Join(", ", _targets.Keys)}");
}
