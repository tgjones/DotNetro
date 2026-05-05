using System.CommandLine;
using Irie;
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

var stopAfter = new Option<string>("--stop-after") { Description = "Stop after the specified pass (for debugging)" };

var startAt = new Option<string>("--start-at") { Description = "Start at the specified pass, reading input as MIR (skips earlier passes)" };

var rootCommand = new RootCommand("Irie lowering tool — translates IR to target Machine IR");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);
rootCommand.Options.Add(startAt);

rootCommand.SetAction(parseResult =>
{
    var input  = parseResult.GetValue(inputArgument);
    var target = parseResult.GetValue(targetOption) ?? "mos6502";
    var stopAfterPass = parseResult.GetValue(stopAfter);
    var startAtPass   = parseResult.GetValue(startAt);

    if (target != "mos6502")
        throw new ArgumentException($"Unsupported target '{target}'. Only 'mos6502' is supported.");

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    var targetInfo = new MOS6502MIRInfo();

    CompilationContext context;
    if (startAtPass != null)
    {
        // MIR input: parse using MOS6502 target info and skip early passes.
        var machineModule = MachineModule.Parse(inputReader, targetInfo);
        context = new CompilationContext(machineModule);
    }
    else
    {
        var irModule = IRModule.Parse(inputReader);
        context = new CompilationContext(irModule, targetInfo);
    }

    if (inputReader != Console.In)
        inputReader.Dispose();

    var passMgr = new PassManager(stopAfterPass, startAtPass);
    passMgr.AddPass(new IRTranslatorPass(new MOS6502CallLowering()));
    passMgr.AddPass(new LegalizerPass(new MOS6502LegalizerInfo()));
    passMgr.AddPass(new InstructionSelectorPass(new MOS6502InstructionSelector()));
    passMgr.AddPass(new PhiEliminationPass());
    passMgr.AddPass(new TwoAddressInstructionPass(
        opcode => MOS6502InstructionInfo.TryGet(opcode)?.TiedOperands));
    passMgr.AddPass(new RegisterCoalescerPass());
    passMgr.AddPass(new RegisterAllocatorPass(new MOS6502RegisterInfo()));
    passMgr.AddPass(new CopyEliminationPass());
    passMgr.Run(context);

    context.MachineModule.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
