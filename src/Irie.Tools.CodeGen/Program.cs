using System.CommandLine;
using Irie.CodeGen;
using Irie.Target.MOS6502;

var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };

var targetOption = new Option<string>("--target") { Description = "Target architecture (currently only 'mos6502')" };

var rootCommand = new RootCommand("Irie CodeGen IR tool — parses Machine IR text and reprints it");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);

rootCommand.SetAction(parseResult =>
{
    var input  = parseResult.GetValue(inputArgument);
    var target = parseResult.GetValue(targetOption) ?? "mos6502";

    if (target != "mos6502")
        throw new ArgumentException($"Unsupported target '{target}'. Only 'mos6502' is supported.");

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    var module = MachineModule.Parse(inputReader, new MOS6502MIRInfo());

    if (inputReader != Console.In)
        inputReader.Dispose();

    module.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
