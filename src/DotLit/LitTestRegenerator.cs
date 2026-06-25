using System.Text;
using System.Text.RegularExpressions;

using DotLit.Model;

namespace DotLit;

/// <summary>
/// Rewrites the CHECK directives in a lit test file (or folder of files) so they match the
/// current output of the file's RUN commands. This is the regeneration that used to be done with
/// throwaway scripts when iterating on the compiler.
///
/// Semantics ("envelope fill"): each maximal run of consecutive CHECK lines that share a label is
/// treated as one block. The block is anchored in the RUN output by matching its existing patterns
/// in order (advancing a per-label cursor, exactly like the executor). The block is then replaced by
/// a *complete listing* of every output line between the first and last line its patterns matched —
/// interior gaps are filled, but output before the first match and after the last match is left
/// unchecked. This refreshes single-block goldens (growing/shrinking with codegen) while keeping the
/// per-function windows of threaded files in place.
/// </summary>
public static class LitTestRegenerator
{
    private static readonly string[] FilePatterns = ["*.irie", "*.mir", "*.s", "*.cs"];

    public static IReadOnlyList<FileRegenerationResult> RegenerateFolder(string folderPath, Func<string, LitTestConfiguration> configurationForFile)
    {
        var files = FilePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(folderPath, pattern, SearchOption.AllDirectories))
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal);

        var results = new List<FileRegenerationResult>();
        foreach (var file in files)
            results.Add(RegenerateFile(file, configurationForFile(file)));
        return results;
    }

    public static FileRegenerationResult RegenerateFile(string filePath, LitTestConfiguration configuration)
    {
        var absolutePath = Path.GetFullPath(filePath);
        var raw = File.ReadAllText(absolutePath);
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        var warnings = new List<string>();

        // Find the CHECK blocks in source order. Files without any CHECK directives (e.g. RUN/DIFF
        // only) need no work — and crucially we avoid running their RUN pipelines.
        var blocks = FindCheckBlocks(lines, configuration, absolutePath);
        if (blocks.Count == 0)
            return new FileRegenerationResult(absolutePath, Changed: false, []);

        // Capture the output of the RUN commands, but only for the labels that have CHECK blocks.
        var neededLabels = blocks.Select(b => b.Label).ToHashSet();
        var testFile = LitTestParser.ParseFile(absolutePath, configuration);
        var runsByLabel = testFile.Commands.OfType<RunCommand>()
            .Where(r => neededLabels.Contains(r.Label))
            .GroupBy(r => r.Label)
            .ToDictionary(g => g.Key, g => g.ToList());

        var outputLinesByLabel = new Dictionary<string, string[]>();
        foreach (var (label, runs) in runsByLabel)
        {
            var combined = new StringBuilder();
            foreach (var run in runs)
                combined.Append(LitTestExecutor.CaptureRunOutput(run.CommandLine));
            outputLinesByLabel[label] = SplitOutput(combined.ToString());
        }

        // Anchor and rewrite each block, advancing a per-label cursor over the output.
        var cursorByLabel = new Dictionary<string, int>();
        var replacements = new Dictionary<int, (int EndLine, List<string> NewLines)>();
        var changed = false;

        foreach (var block in blocks)
        {
            if (!outputLinesByLabel.TryGetValue(block.Label, out var output))
            {
                var prefix = block.Label == "" ? "CHECK" : $"CHECK-{block.Label}";
                warnings.Add($"no RUN{(block.Label == "" ? "" : $"-{block.Label}")} directive for {prefix} block at line {block.StartLine + 1}; left unchanged");
                continue;
            }

            var cursor = cursorByLabel.GetValueOrDefault(block.Label, 0);
            var envelope = MatchEnvelope(output, block.Patterns, ref cursor);
            if (envelope is null)
            {
                warnings.Add($"could not anchor CHECK block at line {block.StartLine + 1} in the RUN output; left unchanged");
                continue;
            }

            cursorByLabel[block.Label] = cursor;

            var newLines = envelope
                .Select(outputLine => FormatCheckLine(block.Prefix, block.Directive, outputLine))
                .ToList();

            var originalLines = lines[block.StartLine..(block.EndLine + 1)];
            if (!newLines.SequenceEqual(originalLines))
                changed = true;

            replacements[block.StartLine] = (block.EndLine, newLines);
        }

        if (changed)
        {
            var rebuilt = new List<string>();
            for (var i = 0; i < lines.Length;)
            {
                if (replacements.TryGetValue(i, out var replacement))
                {
                    rebuilt.AddRange(replacement.NewLines);
                    i = replacement.EndLine + 1;
                }
                else
                {
                    rebuilt.Add(lines[i]);
                    i++;
                }
            }

            File.WriteAllText(absolutePath, string.Join(newline, rebuilt));
        }

        return new FileRegenerationResult(absolutePath, changed, [.. warnings]);
    }

    /// <summary>Splits captured RUN output into lines, dropping the trailing newline's empty line.</summary>
    private static string[] SplitOutput(string output)
    {
        var normalized = output.Replace("\r\n", "\n");
        var split = normalized.Split('\n');
        if (split.Length > 0 && split[^1].Length == 0)
            split = split[..^1];
        return split;
    }

    private static List<CheckBlock> FindCheckBlocks(string[] lines, LitTestConfiguration configuration, string filePath)
    {
        var blocks = new List<CheckBlock>();
        CheckBlock? current = null;

        void Flush()
        {
            if (current is not null)
            {
                blocks.Add(current);
                current = null;
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = LitTestParser.MatchDirective(line);

            var isCheck = match.Success
                && (match.Groups[1].Value == "CHECK" || match.Groups[1].Value.StartsWith("CHECK-"));

            if (!isCheck)
            {
                Flush();
                continue;
            }

            var directive = match.Groups[1].Value;
            var label = directive.Length > 5 ? directive[6..] : "";
            var prefix = line[..match.Index];
            var pattern = LitTestParser.SubstituteVariables(match.Groups[2].Value.Trim(), configuration, filePath);

            if (current is not null && current.Label == label && current.EndLine == i - 1)
            {
                current.EndLine = i;
                current.Patterns.Add(pattern);
            }
            else
            {
                Flush();
                current = new CheckBlock(label, directive, prefix, i);
                current.Patterns.Add(pattern);
            }
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// Matches the block's existing patterns against the output, in order, advancing a cursor.
    /// Returns the inclusive run of output lines from the first to the last matched line, or null if
    /// no pattern matched (the block could not be anchored).
    /// </summary>
    private static string[]? MatchEnvelope(string[] output, List<string> patterns, ref int cursor)
    {
        var searchFrom = cursor;
        var first = -1;
        var last = -1;

        foreach (var pattern in patterns)
        {
            var idx = FindLineMatch(output, searchFrom, pattern);
            if (idx < 0)
                continue;

            if (first < 0)
                first = idx;
            last = idx;
            searchFrom = idx + 1;
        }

        if (first < 0)
            return null;

        cursor = last + 1;
        return output[first..(last + 1)];
    }

    private static int FindLineMatch(string[] output, int from, string pattern)
    {
        var regex = LitPattern.ToRegex(pattern);
        for (var i = Math.Max(from, 0); i < output.Length; i++)
        {
            try
            {
                if (Regex.IsMatch(output[i], regex))
                    return i;
            }
            catch (RegexParseException)
            {
                return -1;
            }
        }
        return -1;
    }

    // CHECK patterns are matched literally (LitPattern), so the regenerated line is the verbatim
    // output line — no escaping. Authors add {{...}} regex holes by hand where output varies.
    private static string FormatCheckLine(string prefix, string directive, string outputLine) =>
        outputLine.Length == 0
            ? $"{prefix}{directive}:"
            : $"{prefix}{directive}: {outputLine}";

    private sealed class CheckBlock(string label, string directive, string prefix, int startLine)
    {
        public string Label { get; } = label;
        public string Directive { get; } = directive;
        public string Prefix { get; } = prefix;
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = startLine;
        public List<string> Patterns { get; } = [];
    }
}

public sealed record FileRegenerationResult(string FilePath, bool Changed, string[] Warnings);
