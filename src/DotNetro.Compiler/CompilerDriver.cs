using System.Collections.ObjectModel;

using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    // IL→MIR pipeline: translate the IL to an Irie MirModule, run it through
    // the MOS6502 BBC Micro pass pipeline, and produce program (raw) + image
    // (.ssd) bytes. Mirrors Irie.Tools.Compiler's Program.cs.
    public static CompilationResult Compile(string dotNetAssemblyPath, string entryPointMethodName)
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
        passMgr.AddPass(new MirSelectLoweringPass());
        passMgr.AddPass(new InstructionSelectorPass(target.InstructionSelector));
        passMgr.AddPass(new PhiEliminationPass(target.BranchLowering));
        passMgr.AddPass(new TwoAddressInstructionPass());
        passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
        passMgr.AddPass(new CopyEliminationPass());
        target.AddPostRegisterAllocationPasses(passMgr);
        // Expand abstract frame accesses (mos6502.frame.*.byte) into concrete
        // addressing sequences per each slot's placement — the eliminateFrameIndex
        // analogue. Post-RA (value already in its physreg, scratch reserved), after
        // the target's placement/addressing passes, before PEI/PseudoExpansion.
        passMgr.AddPass(new FrameAccessLoweringPass(target.FrameLowering));
        passMgr.AddPass(new PrologueEpilogueInsertionPass(target.RegisterInfo, target.FrameLowering));
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

        // The MIR pipeline does not produce an assembly-text listing; the
        // AssemblyCode / Listing fields stay empty (the --emit assembly path
        // produces no output).
        return new CompilationResult(
            string.Empty,
            string.Empty,
            new ReadOnlyCollection<byte>(program),
            new ReadOnlyCollection<byte>(image));
    }
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
