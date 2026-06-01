using System.CommandLine;
using System.Text;
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

var emitOption = new Option<string>("--emit") { Description = "Output form: 'mir' (default) writes post-pipeline MIR text; 'asm' writes target assembly text; 'mc' writes structured MachineCode binary to stdout" };

var rootCommand = new RootCommand("Irie lowering tool — translates MIR to target machine code");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);
rootCommand.Options.Add(startAt);
rootCommand.Options.Add(runPass);
rootCommand.Options.Add(emitOption);

rootCommand.SetAction(parseResult =>
{
    var input         = parseResult.GetValue(inputArgument);
    var targetName    = parseResult.GetValue(targetOption) ?? "mos6502";
    var runPassName   = parseResult.GetValue(runPass);
    var stopAfterPass = runPassName ?? parseResult.GetValue(stopAfter);
    var startAtPass   = runPassName ?? parseResult.GetValue(startAt);
    var emit          = parseResult.GetValue(emitOption) ?? "mir";

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

    switch (emit)
    {
        case "mir":
            module.Write(Console.Out, target.GetRegisterName);
            break;
        case "asm":
        {
            var mc = target.MachineCodeEmitter.Emit(module);
            // MOS6502 is the only target today; if a second target lands this
            // hardcoded writer choice becomes a switch on targetName.
            MOS6502AssemblyWriter.Write(mc, Console.Out);
            break;
        }
        case "mc":
        {
            var mc = target.MachineCodeEmitter.Emit(module);
            using var bw = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8, leaveOpen: true);
            mc.Write(bw);
            break;
        }
        default:
            throw new ArgumentException($"Unknown --emit '{emit}' (expected 'mir', 'asm', or 'mc').");
    }

    if (inputReader != Console.In)
        inputReader.Dispose();
});

return await rootCommand.Parse(args).InvokeAsync();
