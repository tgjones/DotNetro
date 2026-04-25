using System.CommandLine;
using Irie.MachineCode;
using Irie.Target.MOS6502;

var assembleOption = new Option<bool>("--assemble") { Description = "Parse assembly text and write binary" };
var disassembleOption = new Option<bool>("--disassemble") { Description = "Read binary and write assembly text" };
var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };
var outputOption = new Option<FileInfo?>("-o") { Description = "Path to the output file, or - for stdout" };

var rootCommand = new RootCommand("Irie machine code tool");
rootCommand.Options.Add(assembleOption);
rootCommand.Options.Add(disassembleOption);
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(outputOption);

rootCommand.SetAction(parseResult =>
{
    var assemble = parseResult.GetValue(assembleOption);
    var disassemble = parseResult.GetValue(disassembleOption);
    var input = parseResult.GetValue(inputArgument);
    var output = parseResult.GetValue(outputOption);

    if (assemble == disassemble)
    {
        Console.Error.WriteLine("Error: specify exactly one of --assemble or --disassemble");
        Environment.Exit(1);
    }

    if (assemble)
    {
        var inputReader = (input == null || input == "-")
            ? Console.In
            : new StreamReader(input);

        var module = MOS6502AssemblyParser.Parse(inputReader);

        if (inputReader != Console.In)
            inputReader.Dispose();

        var outputStream = (output == null || output.FullName == "-")
            ? Console.OpenStandardOutput()
            : output.OpenWrite();

        using (outputStream != Console.OpenStandardOutput() ? outputStream : null)
        using (var binaryWriter = new BinaryWriter(outputStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            module.Write(binaryWriter);
        }
    }
    else
    {
        var inputStream = (input == null || input == "-")
            ? Console.OpenStandardInput()
            : File.OpenRead(input);

        MachineCodeModule module;
        using (inputStream != Console.OpenStandardInput() ? inputStream : null)
        using (var binaryReader = new BinaryReader(inputStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            module = MachineCodeModule.Read(binaryReader);
        }

        var outputWriter = (output == null || output.FullName == "-")
            ? Console.Out
            : new StreamWriter(output.FullName);

        using (outputWriter != Console.Out ? outputWriter : null)
        {
            MOS6502AssemblyWriter.Write(module, outputWriter);
        }
    }
});

return await rootCommand.Parse(args).InvokeAsync();
