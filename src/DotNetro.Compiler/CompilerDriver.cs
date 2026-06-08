using System.Collections.ObjectModel;

using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

using Sixty502DotNet.Shared;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    public static CompilationResult Compile(string dotNetAssemblyPath, string entryPointMethodName, bool useMir = false)
    {
        if (useMir)
        {
            return CompileViaMir(dotNetAssemblyPath, entryPointMethodName);
        }

        var assemblyCode = DotNetCompiler.Compile(dotNetAssemblyPath, entryPointMethodName, null);

        var assemblerOutput = Assemble(assemblyCode);

        return new CompilationResult(assemblyCode, assemblerOutput.Listing, assemblerOutput.CompiledProgram, assemblerOutput.CompiledImage);
    }

    // New IL→MIR pipeline: translate the IL to an Irie MirModule, run it through
    // the MOS6502 BBC Micro pass pipeline, and produce program (raw) + image
    // (.ssd) bytes. Mirrors Irie.Tools.Compiler's Program.cs.
    private static CompilationResult CompileViaMir(string dotNetAssemblyPath, string entryPointMethodName)
    {
        var target = new MOS6502BbcMicroTarget();

        MirModule module;
        using (var translator = new IlToMirTranslator(dotNetAssemblyPath, target.GetRuntime()))
        {
            module = translator.Translate(entryPointMethodName);
        }

        var context = new CompilationContext(module);

        var passMgr = new PassManager(null, null);
        passMgr.AddPass(new FrameLoweringPass());
        passMgr.AddPass(new AbiLoweringPass(target.CallLowering));
        passMgr.AddPass(new LegalizerPass(target.LegalizerInfo));
        passMgr.AddPass(new InstructionSelectorPass(target.InstructionSelector));
        passMgr.AddPass(new PhiEliminationPass());
        passMgr.AddPass(new TwoAddressInstructionPass());
        passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
        passMgr.AddPass(new CopyEliminationPass());
        target.AddPostRegisterAllocationPasses(passMgr);
        passMgr.AddPass(new PseudoExpansionPass(target.PseudoExpander));
        // Final pass: fill the copy-scratch vregs PseudoExpansion mints (e.g. for
        // immediate→zp moves) with GPRs dead at each point, then lower them. Must
        // run last so no vregs survive into machine-code emission (plan §3.6).
        passMgr.AddPass(new RegisterScavengingPass(target.RegisterInfo, target.PseudoExpander));
        passMgr.Run(context);

        var origin = target.DefaultOrigin!.Value;
        var machineCode = target.MachineCodeEmitter.Emit(module);
        var program = new MOS6502BinaryEncoder().Encode(machineCode, origin);
        var image = target.PackageImage(program, origin);

        // The MIR pipeline does not produce a Sixty502 listing / assembly text;
        // those fields stay empty (the --emit assembly path is not supported
        // through --mir).
        return new CompilationResult(
            string.Empty,
            string.Empty,
            new ReadOnlyCollection<byte>(program),
            new ReadOnlyCollection<byte>(image));
    }

    private static AssemblerResult Assemble(string assemblyCode)
    {
        var options = new Options
        {
            OutputOptions = new OutputOptions
            {
                Format = "bbcmicro",
            },
        };

        Interpreter interpreter = new(options, new FileSystemBinaryReader(""));
        AssemblyState state = interpreter.Exec(assemblyCode, new StringSourceFactory());

        foreach (var error in state.Errors)
        {
            throw new Exception($"Assembly error. Assembly code: {assemblyCode}. Error: {error}", error);
        }

        var listing = string.Join(System.Environment.NewLine, state.StatementListings);

        var objectBytes = state.Output.GetCompilation();

        var outputFormatInfo = new OutputFormatInfo("foo", state.Output.ProgramStart, objectBytes);

        return new AssemblerResult(
            listing,
            objectBytes,
            state.Output.OutputFormat!.GetFormat(outputFormatInfo));
    }

    private sealed record AssemblerResult(string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
