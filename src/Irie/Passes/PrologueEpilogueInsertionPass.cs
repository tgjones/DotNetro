using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// PrologueEpilogueInsertionPass — inserts callee-saved register save/restore
// sequences in each function's prologue/epilogue. Mirrors LLVM's
// PrologEpilogInserter.
// =============================================================================
//
// The framework is target-independent: this pass determines which callee-saved
// registers a function actually clobbers, finds the entry block and the return
// instructions, and drives the target's FrameLowering hooks to emit the actual
// save/restore instructions. Only the instruction emission (and the target's
// notion of "the return instruction") is target-specific.
//
// Pipeline position: after register allocation and any target post-RA passes,
// before PseudoExpansionPass — at which point physreg assignments and the set of
// defined registers are final (matching LLVM's "PEI after RA, before post-RA
// pseudo expansion"). The concrete target ops it emits (e.g. lda.zp/pha/sta.zp)
// are already final machine ops, so the later pseudo-expansion / scavenging
// passes (which only touch pseudo.*) leave them alone.
//
// Currently a no-op for the corpus: no function yet keeps a value in the
// callee-saved pool (RC20..RC31) across a call, because the IL→MIR translator
// still spills cross-call values to .bss. Once that interim is removed, cross-
// call values land in RC20.. and this pass starts emitting prologue/epilogue.
public sealed class PrologueEpilogueInsertionPass(
    TargetRegisterInfo registerInfo, FrameLowering frameLowering) : MirFunctionPass
{
    public override string Name => "PrologueEpilogueInsertion";

    public override void Run(MirFunction function)
    {
        if (function.Blocks.Count == 0)
            return;

        // The callee-saved registers this function actually clobbers: the target's
        // callee-saved list ∩ the physregs that appear as a definition anywhere in
        // the function (including inside surviving pseudo.copy defs). Ascending,
        // deduplicated.
        var calleeSaved = registerInfo.GetCalleeSavedRegisters();
        if (calleeSaved.Length == 0)
            return;

        var definedCalleeSaved = new SortedSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg { IsDefinition: true } p && ContainsReg(calleeSaved, p.Id))
                        definedCalleeSaved.Add(p.Id);

        if (definedCalleeSaved.Count == 0)
            return;

        var saved = definedCalleeSaved.ToList();
        var builder = new MirBuilder(function);

        // Prologue: save in the entry block.
        frameLowering.EmitCalleeSavedSpills(function.Blocks[0], saved, builder);

        // Epilogue: restore before every return. Snapshot the return list first —
        // EmitCalleeSavedRestores mutates the blocks' instruction lists.
        var returns = new List<MirInstruction>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                if (frameLowering.IsReturnInstruction(instr))
                    returns.Add(instr);

        foreach (var returnInstr in returns)
            frameLowering.EmitCalleeSavedRestores(returnInstr, saved, builder);
    }

    private static bool ContainsReg(ReadOnlySpan<int> regs, int reg)
    {
        foreach (var r in regs)
            if (r == reg)
                return true;
        return false;
    }
}
