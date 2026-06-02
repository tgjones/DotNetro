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

        foreach (var (label, runs) in runsByLabel)
        {
            var labelOutput = "";
            foreach (var run in runs)
            {
                var commandOutput = ExecuteRunCommand(run.CommandLine, run.ExpectFailure);
                labelOutput += commandOutput;
            }
            allRunOutput += labelOutput;

            if (!checksByLabel.TryGetValue(label, out var checks))
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

        var output = "";

        process.OutputDataReceived += (sender, e) =>
        {
            output += e.Data + Environment.NewLine;
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            output += e.Data + Environment.NewLine;
        };

        process.Start();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();

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