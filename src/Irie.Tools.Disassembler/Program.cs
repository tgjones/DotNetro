using System.CommandLine;
using Irie.IR;

var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };
var outputOption = new Option<FileInfo?>("-o") { Description = "Path to the output file, or - for stdout" };

var rootCommand = new RootCommand("Irie disassembler");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(outputOption);

rootCommand.SetAction(parseResult =>
{
    var input = parseResult.GetValue(inputArgument);
    var output = parseResult.GetValue(outputOption);

    var inputStream = (input == null || input == "-")
        ? Console.OpenStandardInput()
        : File.OpenRead(input);

    IRModule module;
    using (inputStream != Console.OpenStandardInput() ? inputStream : null)
    using (var binaryReader = new BinaryReader(inputStream, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        module = IRModule.Read(binaryReader);
    }

    var outputWriter = (output == null || output.FullName == "-")
        ? Console.Out
        : new StreamWriter(output.OpenWrite());

    using (outputWriter != Console.Out ? outputWriter : null)
    {
        module.Write(outputWriter);
    }
});

return await rootCommand.Parse(args).InvokeAsync();
