using System.CommandLine;
using System.Text;
using Irie.MachineCode;
using Irie.Target.MOS6502;

var assembleOption = new Option<bool>("--assemble") { Description = "Parse assembly text and write binary" };
var disassembleOption = new Option<bool>("--disassemble") { Description = "Read binary and write assembly text" };
var hexDumpOption = new Option<bool>("--hex-dump") { Description = "Read binary and write a hex-dump text view" };
var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };
var outputOption = new Option<FileInfo?>("-o") { Description = "Path to the output file, or - for stdout" };

var rootCommand = new RootCommand("Irie machine code tool");
rootCommand.Options.Add(assembleOption);
rootCommand.Options.Add(disassembleOption);
rootCommand.Options.Add(hexDumpOption);
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(outputOption);

rootCommand.SetAction(parseResult =>
{
    var assemble = parseResult.GetValue(assembleOption);
    var disassemble = parseResult.GetValue(disassembleOption);
    var hexDump = parseResult.GetValue(hexDumpOption);
    var input = parseResult.GetValue(inputArgument);
    var output = parseResult.GetValue(outputOption);

    var modeCount = (assemble ? 1 : 0) + (disassemble ? 1 : 0) + (hexDump ? 1 : 0);
    if (modeCount != 1)
    {
        Console.Error.WriteLine("Error: specify exactly one of --assemble, --disassemble, or --hex-dump");
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
        using (var binaryWriter = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true))
        {
            module.Write(binaryWriter);
        }
    }
    else if (disassemble)
    {
        var inputStream = (input == null || input == "-")
            ? Console.OpenStandardInput()
            : File.OpenRead(input);

        MachineCodeModule module;
        using (inputStream != Console.OpenStandardInput() ? inputStream : null)
        using (var binaryReader = new BinaryReader(inputStream, Encoding.UTF8, leaveOpen: true))
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
    else
    {
        // --hex-dump: read binary input, write formatted hex.
        var inputStream = (input == null || input == "-")
            ? Console.OpenStandardInput()
            : File.OpenRead(input);

        var outputWriter = (output == null || output.FullName == "-")
            ? Console.Out
            : new StreamWriter(output.FullName);

        using (inputStream != Console.OpenStandardInput() ? inputStream : null)
        using (outputWriter != Console.Out ? outputWriter : null)
        {
            HexDump(inputStream, outputWriter);
        }
    }
});

return await rootCommand.Parse(args).InvokeAsync();

static void HexDump(Stream input, TextWriter output)
{
    var buffer = new byte[16];
    var offset = 0;
    var line = new StringBuilder();

    while (true)
    {
        var bytesRead = ReadExact(input, buffer, 0, buffer.Length);
        if (bytesRead == 0)
            break;

        line.Clear();
        line.Append(offset.ToString("X8"));
        line.Append(": ");

        for (var i = 0; i < bytesRead; i++)
        {
            if (i > 0)
                line.Append(i == 8 ? "  " : " ");
            line.Append(buffer[i].ToString("X2"));
        }

        // Force '\n' line endings (not Environment.NewLine) for deterministic
        // cross-platform output that lit tests can match exactly.
        line.Append('\n');
        output.Write(line.ToString());

        offset += bytesRead;
        if (bytesRead < buffer.Length)
            break;
    }
}

static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
{
    var total = 0;
    while (total < count)
    {
        var read = stream.Read(buffer, offset + total, count - total);
        if (read == 0)
            break;
        total += read;
    }
    return total;
}
