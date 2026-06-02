using System.Collections.Immutable;
using System.Text.RegularExpressions;

using DotLit.Model;

namespace DotLit;

internal static partial class LitTestParser
{
    [GeneratedRegex(@"([A-Z]+(?:-[a-zA-Z0-9][a-zA-Z0-9-]*)?):(.*)")]
    private static partial Regex CommandRegex();

    public static TestFile ParseFile(string filePath, LitTestConfiguration configuration) => Parse(File.ReadAllLines(filePath), filePath, configuration);

    public static TestFile ParseString(string fileContents, string filePath, LitTestConfiguration configuration) => Parse(fileContents.Split(Environment.NewLine), filePath, configuration);

    public static TestFile Parse(string[] fileLines, string filePath, LitTestConfiguration configuration)
    {
        var variables = new Dictionary<string, string>(configuration.Variables)
        {
            ["file"] = filePath
        };

        var commands = new List<TestCommand>();

        foreach (var line in fileLines)
        {
            var match = CommandRegex().Match(line);

            if (match.Success)
            {
                var commandType = match.Groups[1].Value;
                var commandArgs = match.Groups[2].Value.Trim();

                commandArgs = ReplaceVariables(commandArgs, variables);

                if (commandType == "RUN" || commandType.StartsWith("RUN-"))
                {
                    var label = commandType.Length > 3 ? commandType[4..] : "";
                    var expectFailure = commandArgs.StartsWith("not ");
                    commands.Add(new RunCommand(
                        expectFailure ? commandArgs[4..] : commandArgs,
                        ExpectFailure: expectFailure,
                        Label: label));
                }
                else if (commandType == "CHECK" || commandType.StartsWith("CHECK-"))
                {
                    var label = commandType.Length > 5 ? commandType[6..] : "";
                    commands.Add(new CheckCommand(commandArgs, Label: label));
                }
                else
                {
                    throw new InvalidOperationException($"Invalid command {commandType}");
                }
                continue;
            }
        }

        return new TestFile(filePath, [.. commands]);
    }

    private static string ReplaceVariables(string commandArgs, Dictionary<string, string> variables)
    {
        foreach (var (key, value) in variables)
        {
            commandArgs = commandArgs.Replace($"@{key}", value);
        }
        return commandArgs;
    }

}