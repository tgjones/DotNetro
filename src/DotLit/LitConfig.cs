using System.Collections.Immutable;
using System.Text.Json;

namespace DotLit;

/// <summary>
/// Resolves the tool-variable substitutions for a lit test from the nearest <c>lit.config.json</c>
/// in the test's directory tree (LLVM-style ascending lookup). Both the test harness and
/// <c>dotlit --regenerate</c> use this, so the variable -&gt; binary mapping lives only in the test
/// directories, not duplicated in code.
///
/// A <c>lit.config.json</c> is a single object with a <c>substitutions</c> map; each value is a path
/// relative to the config file's own directory and may contain the literal <c>${config}</c>
/// placeholder, filled in with the build configuration (Debug/Release) the caller wants to run
/// against. The file lives at the test project root so the ascending lookup finds it from both a
/// source tree path and a <c>bin/</c> output path:
///
/// <code>
/// { "substitutions": { "iriec": "bin/${config}/net10.0/iriec" } }
/// </code>
/// </summary>
public static class LitConfig
{
    private const string ConfigFileName = "lit.config.json";

    /// <summary>
    /// The build configuration (Debug/Release) of the currently-running assembly, inferred from its
    /// output directory (<c>.../bin/&lt;config&gt;/&lt;tfm&gt;</c>). Lets the harness and the
    /// regenerator resolve tools against whichever configuration they were built/run under.
    /// </summary>
    public static string CurrentBuildConfiguration() =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)[^2];

    public static LitTestConfiguration Load(string litFilePath, string buildConfiguration)
    {
        var fullPath = Path.GetFullPath(litFilePath);
        var startDir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!;

        var configFile = FindAncestorFile(startDir, ConfigFileName)
            ?? throw new FileNotFoundException(
                $"No {ConfigFileName} found in any directory at or above '{fullPath}'.");

        var configDir = Path.GetDirectoryName(configFile)!;

        var variables = ParseSubstitutions(configFile).ToImmutableDictionary(
            entry => entry.Key,
            entry => Path.GetFullPath(Path.Combine(
                configDir,
                entry.Value
                    .Replace("${config}", buildConfiguration)
                    .Replace('/', Path.DirectorySeparatorChar))));

        return new LitTestConfiguration(variables);
    }

    private static Dictionary<string, string> ParseSubstitutions(string configFile)
    {
        using var stream = File.OpenRead(configFile);
        using var document = JsonDocument.Parse(stream);

        var substitutions = new Dictionary<string, string>();
        if (document.RootElement.TryGetProperty("substitutions", out var node))
        {
            foreach (var property in node.EnumerateObject())
                substitutions[property.Name] = property.Value.GetString() ?? "";
        }
        return substitutions;
    }

    private static string? FindAncestorFile(string startDir, string fileName)
    {
        for (var dir = startDir; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
