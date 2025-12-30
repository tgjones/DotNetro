using System.Text.RegularExpressions;

using DotLit.Model;

namespace DotLit;

internal static partial class LitTestParser
{
    [GeneratedRegex("([A-Z]+):(.*)")]
    private static partial Regex CommandRegex();

    public static TestFile ParseFile(string filePath) => Parse(File.ReadAllLines(filePath));

    public static TestFile ParseString(string fileContents) => Parse(fileContents.Split(Environment.NewLine));

    public static TestFile Parse(string[] fileLines)
    {
        var commands = new List<TestCommand>();

        foreach (var line in fileLines)
        {
            var match = CommandRegex().Match(line);

            if (match.Success)
            {
                var commandType = match.Groups[1].Value;
                var commandArgs = match.Groups[2].Value.Trim();

                switch (commandType)
                {
                    case "RUN":
                        commands.Add(new RunCommand(commandArgs));
                        break;

                    case "CHECK":
                        commands.Add(new CheckCommand(commandArgs));
                        break;

                    default:
                        throw new InvalidOperationException($"Invalid command {commandType}");
                }
                continue;
            }
        }

        return new TestFile([.. commands]);
    }
}