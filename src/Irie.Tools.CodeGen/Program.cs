using System.CommandLine;
using Irie.CodeGen;

var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };

var rootCommand = new RootCommand("Irie CodeGen IR tool — parses Machine IR text and reprints it");
rootCommand.Arguments.Add(inputArgument);

rootCommand.SetAction(parseResult =>
{
    var input = parseResult.GetValue(inputArgument);

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    var module = MachineModule.Parse(inputReader);

    if (inputReader != Console.In)
        inputReader.Dispose();

    module.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
