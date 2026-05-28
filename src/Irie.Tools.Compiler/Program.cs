using System.CommandLine;
using Irie;
using Irie.CodeGen;
using Irie.CodeGen.Passes;
using Irie.IR;
using Irie.Target.MOS6502;
using V2Passes = Irie.Passes;
using V2Mir    = Irie.Mir;
using V2MOS6502 = Irie.Target.MOS6502.V2;

TargetRegistry.Register("mos6502", new MOS6502Target());

var inputArgument = new Argument<string?>("input")
{
    Description = "Path to the input IR file, or - for stdin",
    Arity = ArgumentArity.ZeroOrOne,
};

var targetOption = new Option<string>("--target") { Description = "Target architecture (currently only 'mos6502')" };

var stopAfter = new Option<string>("--stop-after") { Description = "Stop after the specified pass (for debugging)" };

var startAt = new Option<string>("--start-at") { Description = "Start at the specified pass, reading input as MIR (skips earlier passes)" };

var runPass = new Option<string>("--run-pass") { Description = "Run only the specified pass (shorthand for --start-at X --stop-after X); use 'none' to parse and reprint with no passes" };

var inputLanguageOption = new Option<string?>("--input-language") { Description = "Input language: 'ir' or 'mir'; auto-detected from file extension if omitted" };

var engineOption = new Option<string>("--engine") { Description = "Pipeline engine: 'v1' (legacy IR/CodeGen, default) or 'v2' (unified-MIR; reads MIR text, runs the new pass list)" };

var rootCommand = new RootCommand("Irie lowering tool — translates IR to target Machine IR");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);
rootCommand.Options.Add(startAt);
rootCommand.Options.Add(runPass);
rootCommand.Options.Add(inputLanguageOption);
rootCommand.Options.Add(engineOption);

rootCommand.SetAction(parseResult =>
{
    var input         = parseResult.GetValue(inputArgument);
    var targetName    = parseResult.GetValue(targetOption) ?? "mos6502";
    var runPassName   = parseResult.GetValue(runPass);
    var stopAfterPass = runPassName ?? parseResult.GetValue(stopAfter);
    var startAtPass   = runPassName ?? parseResult.GetValue(startAt);
    var inputLanguage = parseResult.GetValue(inputLanguageOption);
    var engine        = parseResult.GetValue(engineOption) ?? "v1";

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    if (engine == "v2")
    {
        RunV2(targetName, inputReader, stopAfterPass, startAtPass);
    }
    else if (engine == "v1")
    {
        RunV1(targetName, inputReader, input, inputLanguage, stopAfterPass, startAtPass);
    }
    else
    {
        throw new ArgumentException($"Unknown --engine '{engine}'. Use 'v1' or 'v2'.");
    }

    if (inputReader != Console.In)
        inputReader.Dispose();
});

return await rootCommand.Parse(args).InvokeAsync();

static void RunV1(
    string targetName,
    TextReader inputReader,
    string? input,
    string? inputLanguage,
    string? stopAfterPass,
    string? startAtPass)
{
    var target = TargetRegistry.Get(targetName);

    inputLanguage ??= (input != null && input != "-" && Path.GetExtension(input) == ".mir") ? "mir" : "ir";

    if (inputLanguage != "ir" && inputLanguage != "mir")
        throw new ArgumentException($"Unsupported input language '{inputLanguage}'. Use 'ir' or 'mir'.");

    CompilationContext context;
    if (inputLanguage == "mir")
    {
        var machineModule = MachineModule.Parse(inputReader, target);
        context = new CompilationContext(machineModule);
    }
    else
    {
        var irModule = IRModule.Parse(inputReader);
        context = new CompilationContext(irModule, target);
    }

    var passMgr = new PassManager(stopAfterPass, startAtPass);
    passMgr.AddPass(new IRTranslatorPass(target.CreateCallLowering()));
    passMgr.AddPass(new LegalizerPass(target.CreateLegalizerInfo()));
    passMgr.AddPass(new InstructionSelectorPass(target.CreateInstructionSelector()));
    passMgr.AddPass(new PhiEliminationPass());
    passMgr.AddPass(new TwoAddressInstructionPass(target.CreateInstructionInfo()));
    passMgr.AddPass(new RegisterCoalescerPass());
    var regAllocFlexibleI8Class = target is MOS6502Target ? MOS6502RegisterClass.Anyi8 : 0;
    passMgr.AddPass(new RegisterAllocatorPass(target.CreateRegisterInfo(), target.CreateInstructionInfo(), regAllocFlexibleI8Class));
    passMgr.AddPass(new VirtualRegisterRewriterPass(target.CreateRegisterInfo()));
    passMgr.AddPass(new CopyEliminationPass());
    passMgr.Run(context);

    context.MachineModule.Write(Console.Out);
}

static void RunV2(
    string targetName,
    TextReader inputReader,
    string? stopAfterPass,
    string? startAtPass)
{
    Irie.Target.Target target = targetName switch
    {
        "mos6502" => new V2MOS6502.MOS6502TargetV2(),
        _ => throw new ArgumentException($"Unknown --target '{targetName}' for --engine=v2."),
    };

    var module = V2Mir.MirModule.Parse(inputReader);
    var context = new V2Passes.CompilationContext(module);

    var passMgr = new V2Passes.PassManager(stopAfterPass, startAtPass);
    passMgr.AddPass(new V2Passes.AbiLoweringPass(target.CallLowering));
    passMgr.AddPass(new V2Passes.LegalizerPass(target.LegalizerInfo));
    passMgr.Run(context);

    module.Write(Console.Out, target.GetRegisterName);
}
