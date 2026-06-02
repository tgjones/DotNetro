using System.CommandLine;

var programFileArgument = new Argument<string?>("program-file") { Description = "Path to a raw 6502 program file, or - for stdin.", Arity = ArgumentArity.ZeroOrOne };
var targetSystemOption = new Option<string>("--target-system", "Target system. Currently: bbcmicro (default).")
{
    DefaultValueFactory = _ => "bbcmicro",
};
var inputOption = new Option<string?>("--input", "Console input. Use \\r to separate lines.");
var maxTicksOption = new Option<int>("--max-ticks", "Tick budget before the run is aborted as a runaway. Default: 1,000,000.")
{
    DefaultValueFactory = _ => 1_000_000,
};
var traceOption = new Option<FileInfo?>("--trace", "Write a per-instruction PC/A/X/Y/P/SP trace to file.");

var rootCommand = new RootCommand("DotNetro 6502 emulator");
rootCommand.Arguments.Add(programFileArgument);
rootCommand.Options.Add(targetSystemOption);
rootCommand.Options.Add(inputOption);
rootCommand.Options.Add(maxTicksOption);
rootCommand.Options.Add(traceOption);

rootCommand.SetAction(parseResult =>
{
    var programFile = parseResult.GetValue(programFileArgument);
    var targetSystemName = parseResult.GetValue(targetSystemOption)!;
    var inputString = parseResult.GetValue(inputOption);
    var maxTicks = parseResult.GetValue(maxTicksOption);
    var traceFile = parseResult.GetValue(traceOption);

    byte[] programBytes;
    try
    {
        if (programFile == null || programFile == "-")
        {
            using var ms = new MemoryStream();
            Console.OpenStandardInput().CopyTo(ms);
            programBytes = ms.ToArray();
        }
        else
        {
            programBytes = File.ReadAllBytes(programFile);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading program: {ex.Message}");
        return 1;
    }

    ITargetSystem target;
    try
    {
        target = targetSystemName switch
        {
            "bbcmicro" => new BbcMicroTargetSystem(),
            _ => throw new ArgumentException($"Unknown --target-system '{targetSystemName}'."),
        };
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (programBytes.Length > 0x10000 - target.LoadAddress)
    {
        Console.Error.WriteLine($"Program too large: {programBytes.Length} bytes doesn't fit at ${target.LoadAddress:X4}.");
        return 1;
    }

    var memory = new byte[0x10000];
    programBytes.CopyTo(memory, target.LoadAddress);
    target.InitialiseMemory(memory);

    var inputReader = new StringReader(inputString?.Replace("\\r", "\r") ?? string.Empty);

    StreamWriter? traceWriter = null;
    if (traceFile != null)
    {
        traceWriter = new StreamWriter(traceFile.OpenWrite());
    }

    try
    {
        var result = target.Run(memory, inputReader, traceWriter, maxTicks);
        Console.Write(result.Output);

        return result.Status switch
        {
            EmulationStatus.Completed => 0,
            EmulationStatus.RunawayTimeout => 2,
            _ => 3,
        };
    }
    finally
    {
        traceWriter?.Dispose();
    }
});

return await rootCommand.Parse(args).InvokeAsync();
