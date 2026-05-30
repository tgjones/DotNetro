using System.CommandLine;
using Irie.Mir;
using Irie.Passes;
using Irie.Target;
using Irie.Target.MOS6502;

var inputArgument = new Argument<string?>("input")
{
    Description = "Path to the input MIR file, or - for stdin",
    Arity = ArgumentArity.ZeroOrOne,
};

var targetOption = new Option<string>("--target") { Description = "Target architecture (currently only 'mos6502')" };

var stopAfter = new Option<string>("--stop-after") { Description = "Stop after the specified pass (for debugging)" };

var startAt = new Option<string>("--start-at") { Description = "Start at the specified pass (skips earlier passes)" };

var runPass = new Option<string>("--run-pass") { Description = "Run only the specified pass (shorthand for --start-at X --stop-after X); use 'none' to parse and reprint with no passes" };

var rootCommand = new RootCommand("Irie lowering tool — translates MIR to target machine code");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);
rootCommand.Options.Add(startAt);
rootCommand.Options.Add(runPass);

rootCommand.SetAction(parseResult =>
{
    var input         = parseResult.GetValue(inputArgument);
    var targetName    = parseResult.GetValue(targetOption) ?? "mos6502";
    var runPassName   = parseResult.GetValue(runPass);
    var stopAfterPass = runPassName ?? parseResult.GetValue(stopAfter);
    var startAtPass   = runPassName ?? parseResult.GetValue(startAt);

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    Target target = targetName switch
    {
        "mos6502" => new MOS6502Target(),
        _ => throw new ArgumentException($"Unknown --target '{targetName}'."),
    };

    var module = MirModule.Parse(inputReader);
    var context = new CompilationContext(module);

    var passMgr = new PassManager(stopAfterPass, startAtPass);
    passMgr.AddPass(new AbiLoweringPass(target.CallLowering));
    passMgr.AddPass(new LegalizerPass(target.LegalizerInfo));
    passMgr.AddPass(new InstructionSelectorPass(target.InstructionSelector));
    passMgr.AddPass(new PhiEliminationPass());
    passMgr.AddPass(new TwoAddressInstructionPass());
    passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
    passMgr.AddPass(new CopyEliminationPass());
    target.AddPostRegisterAllocationPasses(passMgr);
    passMgr.AddPass(new PseudoExpansionPass(target.PseudoExpander));
    passMgr.Run(context);

    module.Write(Console.Out, target.GetRegisterName);

    if (inputReader != Console.In)
        inputReader.Dispose();
});

return await rootCommand.Parse(args).InvokeAsync();
