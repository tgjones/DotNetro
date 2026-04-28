using System.CommandLine;
using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.IR;
using Irie.Target.MOS6502;

var inputArgument = new Argument<string?>("input")
{
    Description = "Path to the input IR file, or - for stdin",
    Arity = ArgumentArity.ZeroOrOne,
};

var targetOption = new Option<string>("--target") { Description = "Target architecture (currently only 'mos6502')" };

var rootCommand = new RootCommand("Irie lowering tool — translates IR to target Machine IR");
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

    var irModule = IRModule.Parse(inputReader);

    if (inputReader != Console.In)
        inputReader.Dispose();

    var callLowering = new MOS6502CallLowering();
    var translator   = new IRTranslatorPass(callLowering);

    var passMgr = new MachineFunctionPassManager();
    passMgr.AddPass(new LegalizerPass(new MOS6502LegalizerInfo()));
    passMgr.AddPass(new InstructionSelectorPass(new MOS6502InstructionSelector()));

    var machineModule = new MachineModule
    {
        OpcodeNamer   = MOS6502InstructionInfo.GetDisplayName,
        RegisterNamer = MOS6502Registers.NameOf,
    };

    foreach (var irFunction in irModule.Functions)
    {
        var machineFunction = translator.Translate(irFunction);
        passMgr.Run(machineFunction);
        machineModule.Functions.Add(machineFunction);
    }

    machineModule.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
