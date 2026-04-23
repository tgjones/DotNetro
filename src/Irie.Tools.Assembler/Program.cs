using System.CommandLine;
using Irie.IR;

var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };
var outputOption = new Option<FileInfo?>("-o") { Description = "Path to the output file, or - for stdout" };

var rootCommand = new RootCommand("Irie assembler");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(outputOption);

rootCommand.SetAction(parseResult =>
{
    var input = parseResult.GetValue(inputArgument);
    var output = parseResult.GetValue(outputOption);

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    IRModule module;
    using (inputReader != Console.In ? inputReader : null)
    {
        module = IRModule.Parse(inputReader);
    }

    var outputStream = (output == null || output.FullName == "-")
        ? Console.OpenStandardOutput()
        : output.OpenWrite();

    using (outputStream != Console.OpenStandardOutput() ? outputStream : null)
    using (var binaryWriter = new BinaryWriter(outputStream, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        module.Write(binaryWriter);
    }
});

return await rootCommand.Parse(args).InvokeAsync();
