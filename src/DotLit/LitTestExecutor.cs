using System.Diagnostics;
using System.Text.RegularExpressions;

using DotLit.Model;

namespace DotLit;

internal static class LitTestExecutor
{
    public static LitTestResult Execute(TestFile testFile)
    {
        var errorMessages = new List<string>();
        var successful = true;
        var allRunOutput = "";

        var runsByLabel = testFile.Commands.OfType<RunCommand>()
            .GroupBy(r => r.Label)
            .ToDictionary(g => g.Key, g => g.ToList());

        var checksByLabel = testFile.Commands.OfType<CheckCommand>()
            .GroupBy(c => c.Label)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var label in checksByLabel.Keys)
        {
            if (!runsByLabel.ContainsKey(label))
            {
                var prefix = label == "" ? "CHECK" : $"CHECK-{label}";
                errorMessages.Add($"No RUN directive for label '{label}' (found {prefix}: lines but no matching RUN{(label == "" ? "" : $"-{label}")}:)");
                successful = false;
            }
        }

        var outputsByLabel = new Dictionary<string, string>();
        foreach (var (label, runs) in runsByLabel)
        {
            var labelOutput = "";
            foreach (var run in runs)
            {
                var commandOutput = ExecuteRunCommand(run.CommandLine, run.ExpectFailure);
                labelOutput += commandOutput;
            }
            outputsByLabel[label] = labelOutput;
            allRunOutput += labelOutput;
        }

        foreach (var diff in testFile.Commands.OfType<DiffCommand>())
        {
            if (!outputsByLabel.TryGetValue(diff.Label1, out var output1))
            {
                errorMessages.Add($"DIFF: no RUN-{diff.Label1}: directive found");
                successful = false;
                continue;
            }
            if (!outputsByLabel.TryGetValue(diff.Label2, out var output2))
            {
                errorMessages.Add($"DIFF: no RUN-{diff.Label2}: directive found");
                successful = false;
                continue;
            }
            if (NormalizeOutput(output1) != NormalizeOutput(output2))
            {
                errorMessages.Add($"DIFF failed: output of '{diff.Label1}' does not match '{diff.Label2}'.\n--- {diff.Label1} ---\n{output1}\n--- {diff.Label2} ---\n{output2}");
                successful = false;
            }
        }

        foreach (var (label, checks) in checksByLabel)
        {
            if (!outputsByLabel.TryGetValue(label, out var labelOutput))
                continue;

            var currentCharIndex = 0;
            foreach (var check in checks)
            {
                var match = Regex.Match(labelOutput[currentCharIndex..], check.Pattern);
                if (!match.Success)
                {
                    var prefix = label == "" ? "CHECK" : $"CHECK-{label}";
                    errorMessages.Add($"{prefix} failed; couldn't find `{check.Pattern}` in command output `{labelOutput}`");
                    successful = false;
                    break;
                }
                currentCharIndex += match.Index + match.Length;
            }
        }

        return new LitTestResult(successful, [.. errorMessages], allRunOutput);
    }

    private static string NormalizeOutput(string output) =>
        output.Replace("\r\n", "\n");

    private static string ExecuteRunCommand(string commandLine, bool expectFailure = false)
    {
        using var process = new Process();

        var (shell, shellFlag) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/C")
            : ("/bin/sh", "-c");

        process.StartInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add(shellFlag);
        process.StartInfo.ArgumentList.Add(commandLine);

        var outputBuilder = new System.Text.StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
                outputBuilder.Append(e.Data + Environment.NewLine);
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
                outputBuilder.Append(e.Data + Environment.NewLine);
        };

        process.Start();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();

        var output = outputBuilder.ToString();

        if (expectFailure)
        {
            if (process.ExitCode == 0)
                throw new Exception($"Command `{commandLine}` was expected to fail but succeeded. Output: {output}");
        }
        else
        {
            if (process.ExitCode != 0)
                throw new Exception($"Command `{commandLine}` failed with exit code {process.ExitCode}. Output: {output}");
        }

        return output;
    }
}

public sealed record LitTestResult(bool Successful, string[] ErrorMessages, string RunOutput);