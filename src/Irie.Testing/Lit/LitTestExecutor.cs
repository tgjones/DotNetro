using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Irie.Testing.Lit;

public static class LitTestExecutor
{
    public static void Execute(string filePath)
    {
        var testFile = LitTestParser.Parse(filePath);

        foreach (var command in testFile.Commands.OfType<LitRunCommand>())
        {
            var commandOutput = ExecuteRunCommand(command.CommandLine);

            foreach (var checkCommand in testFile.Commands.OfType<LitCheckCommand>())
            {
                // Walk through commandOutput, executing checks and advancing position.
                //if (!Regex.IsMatch(commandOutput, checkCommand.Pattern))
                //{
                //    throw new InvalidOperationException($"Check failed: {checkCommand.Pattern}");
                //}
            }
        }
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

public static partial class LitTestParser
{
    [GeneratedRegex("([A-Z]+):(.*)")]
    private static partial Regex CommandRegex();

    public static LitTestFile Parse(string filePath)
    {
        var commands = new List<LitTestCommand>();

        foreach (var line in File.ReadLines(filePath))
        {
            var match = CommandRegex().Match(line);
            
            if (match.Success)
            {
                var commandType = match.Groups[1].Value;
                var commandArgs = match.Groups[2].Value.Trim();

                switch (commandType)
                {
                    case "RUN":
                        commands.Add(new LitRunCommand(commandArgs));
                        break;

                    default:
                        throw new InvalidOperationException($"Invalid command {commandType}");
                }
                continue;
            }
        }

        return new LitTestFile(filePath, [.. commands]);
    }
}

public sealed record LitTestFile(string FilePath, LitTestCommand[] Commands);

public abstract record LitTestCommand();

public sealed record LitRunCommand(string CommandLine) : LitTestCommand();

public sealed record LitCheckCommand(string Pattern) : LitTestCommand();