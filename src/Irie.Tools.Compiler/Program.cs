using System.CommandLine;
using Irie;
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

var rootCommand = new RootCommand("Irie lowering tool — translates IR to target Machine IR");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(stopAfter);

rootCommand.SetAction(parseResult =>
{
    var input  = parseResult.GetValue(inputArgument);
    var target = parseResult.GetValue(targetOption) ?? "mos6502";
    var stopAfterPass = parseResult.GetValue(stopAfter);

    if (target != "mos6502")
        throw new ArgumentException($"Unsupported target '{target}'. Only 'mos6502' is supported.");

    var inputReader = (input == null || input == "-")
        ? Console.In
        : new StreamReader(input);

    var irModule = IRModule.Parse(inputReader);

    if (inputReader != Console.In)
        inputReader.Dispose();

    var context = new CompilationContext(irModule);

    var passMgr = new PassManager(stopAfterPass);
    passMgr.AddPass(new IRTranslatorPass(new MOS6502CallLowering()));
    passMgr.AddPass(new LegalizerPass(new MOS6502LegalizerInfo()));
    passMgr.AddPass(new InstructionSelectorPass(new MOS6502InstructionSelector()));
    passMgr.AddPass(new PhiEliminationPass());
    passMgr.AddPass(new RegisterAllocatorPass(new MOS6502RegisterInfo()));
    passMgr.AddPass(new CopyEliminationPass());
    passMgr.Run(context);

    context.MachineModule.OpcodeNamer          = MOS6502InstructionInfo.GetDisplayName;
    context.MachineModule.RegisterNamer        = MOS6502Registers.NameOf;
    context.MachineModule.RegisterClassNamer   = MOS6502RegisterClass.GetName;
    context.MachineModule.TiedOperandsProvider = opcode => MOS6502InstructionInfo.TryGet(opcode)?.TiedOperands;

    context.MachineModule.Write(Console.Out);
});

return await rootCommand.Parse(args).InvokeAsync();
