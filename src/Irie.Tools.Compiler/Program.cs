using System.CommandLine;
using Irie;
using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.IR;
using Irie.Target.MOS6502;

TargetRegistry.Register("mos6502", new MOS6502Target());

var inputArgument = new Argument<string?>("input")
{
    Description = "Path to the input IR file, or - for stdin",
    Arity = ArgumentArity.ZeroOrOne,
};

var targetOption = new Option<string>("--target") { Description = "Target architecture (currently only 'mos6502')" };

var stopAfter = new Option<string>("--stop-after") { Description = "Stop after the specified pass (for debugging)" };

var startAt = new Option<string>("--start-at") { Description = "Start at the specified pass, reading input as MIR (skips earlier passes)" };

var runPass = new Option<string>("--run-pass") { Description = "Run only the specified pass (shorthand for --start-at X --stop-after X)" };

var inputLanguageOption = new Option<string?>("--input-language") { Description = "Input language: 'ir' or 'mir'; auto-detected from file extension if omitted" };

var rootCommand = new RootCommand("Irie lowering tool — translates IR to target Machine IR");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);
rootCommand.Options.Add(startAt);
rootCommand.Options.Add(runPass);
rootCommand.Options.Add(inputLanguageOption);

rootCommand.SetAction(parseResult =>
{
    var input         = parseResult.GetValue(inputArgument);
    var targetName    = parseResult.GetValue(targetOption) ?? "mos6502";
    var runPassName   = parseResult.GetValue(runPass);
    var stopAfterPass = runPassName ?? parseResult.GetValue(stopAfter);
    var startAtPass   = runPassName ?? parseResult.GetValue(startAt);
    var inputLanguage = parseResult.GetValue(inputLanguageOption);

    var target  = TargetRegistry.Get(targetName);
    var mirInfo = target.CreateMIRInfo();

    inputLanguage ??= (input != null && input != "-" && Path.GetExtension(input) == ".mir") ? "mir" : "ir";

    if (inputLanguage != "ir" && inputLanguage != "mir")
        throw new ArgumentException($"Unsupported input language '{inputLanguage}'. Use 'ir' or 'mir'.");

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    CompilationContext context;
    if (inputLanguage == "mir")
    {
        var machineModule = MachineModule.Parse(inputReader, mirInfo);
        context = new CompilationContext(machineModule);
    }
    else
    {
        var irModule = IRModule.Parse(inputReader);
        context = new CompilationContext(irModule, mirInfo);
    }

    if (inputReader != Console.In)
        inputReader.Dispose();

    var passMgr = new PassManager(stopAfterPass, startAtPass);
    passMgr.AddPass(new IRTranslatorPass(target.CreateCallLowering()));
    passMgr.AddPass(new LegalizerPass(target.CreateLegalizerInfo()));
    passMgr.AddPass(new InstructionSelectorPass(target.CreateInstructionSelector()));
    passMgr.AddPass(new PhiEliminationPass());
    passMgr.AddPass(new TwoAddressInstructionPass(opcode => mirInfo.GetTiedOperands(opcode)));
    passMgr.AddPass(new RegisterCoalescerPass());
    passMgr.AddPass(new RegisterAllocatorPass(target.CreateRegisterInfo()));
    passMgr.AddPass(new CopyEliminationPass());
    passMgr.Run(context);

    context.MachineModule.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
