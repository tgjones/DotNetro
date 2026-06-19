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
        passMgr.AddPass(new GenericMirVerifierPass());
        passMgr.AddPass(new ArithSimplifyPass());
        passMgr.AddPass(new ReturnMergePass());
        passMgr.AddPass(new FrameLoweringPass());
        passMgr.AddPass(new AbiLoweringPass(target.CallLowering));
        passMgr.AddPass(new LegalizerPass(target.LegalizerInfo));
        // Target pre-isel passes (e.g. MOS6502 select-lowering) run on legalized,
        // still-SSA MIR — the llvm-mos slot for MOSLowerSelect.
        target.AddPreInstructionSelectionPasses(passMgr);
        passMgr.AddPass(new InstructionSelectorPass(target.InstructionSelector));
        passMgr.AddPass(new PhiEliminationPass(target.BranchLowering));
        passMgr.AddPass(new TwoAddressInstructionPass());
        passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
        // Target branch-folding passes run on physreg-only MIR after RA and
        // before CopyElimination — the llvm-mos Control Flow Optimizer slot
        // (after the VReg rewriter, before copy-opt). MOS6502 uses this for
        // empty-block elimination.
        target.AddBranchFoldingPasses(passMgr);
        passMgr.AddPass(new CopyEliminationPass());
        target.AddPostRegisterAllocationPasses(passMgr);
        // Expand abstract frame accesses (mos6502.frame.*.byte) into concrete
        // addressing sequences per each slot's placement — the eliminateFrameIndex
        // analogue. Post-RA (value already in its physreg, scratch reserved), after
        // the target's placement/addressing passes, before PEI/PseudoExpansion.
        passMgr.AddPass(new FrameAccessLoweringPass(target.FrameLowering));
        passMgr.AddPass(new PrologueEpilogueInsertionPass(target.RegisterInfo, target.FrameLowering));
        passMgr.AddPass(new PseudoExpansionPass(target.PseudoExpander));
        // Target late-optimization passes run on the final expanded physreg-only
        // stream — the llvm-mos `mos-late-opt` slot (after post-RA pseudo
        // expansion). MOS6502 uses this for the flag-from-load fold.
        target.AddPostPseudoExpansionPasses(passMgr);
        // Final pass: fill the copy-scratch vregs PseudoExpansion mints (e.g. for
        // immediate→zp moves) with GPRs dead at each point, then lower them. Must
        // run last so no vregs survive into machine-code emission (plan §3.6).
        passMgr.AddPass(new RegisterScavengingPass(target.RegisterInfo, target.PseudoExpander));
        // Target final passes run after scavenging — the llvm-mos block-placement
        // slot. Block-placement drops fall-through jmps (making CFG edges
        // implicit), so it must run after scavenging, which still needs the
        // explicit-jmp CFG to compute physreg liveness.
        target.AddFinalPasses(passMgr);
        // Tail-duplication runs last — after the target's block-placement — to
        // rebalance trivial shared return tails left by ReturnMergePass (the
        // llvm-mos `tailduplication` analogue). See TailDuplicationPass.
        passMgr.AddPass(new TailDuplicationPass(target.BranchLowering));
        passMgr.Run(context);

        var origin = target.DefaultOrigin!.Value;
        var machineCode = target.MachineCodeEmitter.Emit(module);
        var program = new MOS6502BinaryEncoder().Encode(machineCode, origin);
        var image = target.PackageImage(program, origin);

        var machineCodeText = "";
        if (machineCode != null)
        {
            var machineCodeWriter = new StringWriter();
            MOS6502AssemblyWriter.Write(machineCode, machineCodeWriter);
            machineCodeText = machineCodeWriter.ToString();
        }

        // The MIR pipeline does not produce an assembly-text listing; the
        // AssemblyCode / Listing fields stay empty (the --emit assembly path
        // produces no output).
        return new CompilationResult(
            machineCodeText,
            string.Empty,
            new ReadOnlyCollection<byte>(program),
            new ReadOnlyCollection<byte>(image));
    }
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
