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

        return output;
    }
}

public sealed record LitTestResult(bool Successful, string[] ErrorMessages, string RunOutput);