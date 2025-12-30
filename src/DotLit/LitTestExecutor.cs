using System.Diagnostics;
using System.Text.RegularExpressions;

using DotLit.Model;

namespace DotLit;

internal static class LitTestExecutor
{
    public static LitTestResult Execute(TestFile testFile)
    {
        var combinedRunOutput = "";
        var errorMessages = new List<string>();
        var successful = true;

        foreach (var command in testFile.Commands.OfType<RunCommand>())
        {
            var commandOutput = ExecuteRunCommand(command.CommandLine);
            combinedRunOutput += commandOutput;
        }

        var currentCharIndex = 0;

        foreach (var checkCommand in testFile.Commands.OfType<CheckCommand>())
        {
            var substring = combinedRunOutput.AsSpan(currentCharIndex);

            var match = Regex.Match(combinedRunOutput, checkCommand.Pattern);

            if (!match.Success)
            {
                errorMessages.Add($"CHECK failed; couldn't find `{checkCommand.Pattern}` in command output `{combinedRunOutput}`");
                successful = false;
                break;
            }

            currentCharIndex = match.Index + match.Length;
        }

        return new LitTestResult(successful, [.. errorMessages], combinedRunOutput);
    }

    private static string ExecuteRunCommand(string commandLine)
    {
        using var process = new Process();

        process.StartInfo = new ProcessStartInfo("cmd.exe", $"/C {commandLine}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

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

        return output;
    }
}

public sealed record LitTestResult(bool Successful, string[] ErrorMessages, string RunOutput);