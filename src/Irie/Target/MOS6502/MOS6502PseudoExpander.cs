using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// Lowers `pseudo.copy` to MOS6502 moves. Called by PseudoExpansionPass for
// every surviving `pseudo.copy` after RA, copy elimination, and addressing-mode
// selection. Operands are physregs only by this point (vregs are gone after RA);
// the only non-physreg source kind we expect is `Immediate` (from a constant
// that wasn't materialised earlier).
//
// Direct moves cover the common cases (transfer instructions for A↔X/Y, load/
// store-zp for A/X/Y ↔ RC, immediate loads for A/X/Y). Pairs the 6502 can't do
// in one instruction ("impossible pairs" in unified-IR plan §5.6) go through a
// scratch register.
//
// Copy scratch (plan §3.6): a copy that needs a temporary register to lower —
// an immediate materialised into a zero-page slot needs a GPR for `LD? #imm ;
// ST? $zp` — is NOT lowered eagerly through a hardcoded $a. Instead it is
// re-emitted as a 3-operand "scratch form" `pseudo.copy %dst, %src, %scratch`
// whose third operand is a FRESH virtual register. The post-RA
// RegisterScavengingPass then assigns that scratch vreg the cheapest GPR dead
// at the copy's point and calls this expander back on the now-physreg-only
// 3-operand form, which lowers it to the final two machine instructions. This
// lets scratch be $x/$y when those are free, instead of forcing $a free for
// every such copy (the old GetPseudoCopyScratchClobbers approach, now deleted).
//
// (Other scratch-needing copies — zp→zp, $x↔$y — are physreg→physreg copies
// handled earlier by MOS6502ParallelCopyPass, which evacuates $a before they
// reach this expander; so the only scratch form minted here is immediate→zp.)
public sealed class MOS6502PseudoExpander : Irie.Target.PseudoExpander
{
    public override void Expand(MirInstruction copy, MirBuilder builder)
    {
        // 3-operand scratch form: %dst = pseudo.copy %src using %scratch.
        // Emitted by this same expander for a scratch-needing copy and filled
        // in by RegisterScavengingPass, which calls us back to do the final
        // lowering. By now operand[2] is a concrete scratch physreg.
        if (copy.Operands.Length == 3)
        {
            ExpandWithScratch(copy, builder);
            return;
        }

        if (copy.Operands.Length != 2)
            throw new InvalidOperationException(
                $"MOS6502PseudoExpander: pseudo.copy must have 2 or 3 operands, got {copy.Operands.Length}.");

        if (copy.Operands[0] is not PhysicalReg dst || !dst.IsDefinition)
            throw new InvalidOperationException(
                "MOS6502PseudoExpander: pseudo.copy destination must be a physical register def.");

        switch (copy.Operands[1])
        {
            case PhysicalReg src:
                ExpandRegToReg(dst.Id, src.Id, builder);
                return;
            case Immediate imm:
                ExpandImmediateToReg(dst.Id, imm.Value, builder);
                return;
            default:
                throw new InvalidOperationException(
                    $"MOS6502PseudoExpander: unsupported pseudo.copy source operand " +
                    $"{copy.Operands[1].GetType().Name}.");
        }
    }

    private static void ExpandRegToReg(int dst, int src, MirBuilder builder)
    {
        if (dst == src) return;                          // identity (CopyElim usually catches these)

        var srcIsZp = IsZeroPage(src);
        var dstIsZp = IsZeroPage(dst);

        // Architectural transfers (A ↔ X, A ↔ Y).
        if (src == MOS6502Registers.A && dst == MOS6502Registers.X) { Emit(builder, MOS6502Op.Tax, dst, src); return; }
        if (src == MOS6502Registers.A && dst == MOS6502Registers.Y) { Emit(builder, MOS6502Op.Tay, dst, src); return; }
        if (src == MOS6502Registers.X && dst == MOS6502Registers.A) { Emit(builder, MOS6502Op.Txa, dst, src); return; }
        if (src == MOS6502Registers.Y && dst == MOS6502Registers.A) { Emit(builder, MOS6502Op.Tya, dst, src); return; }

        // Zero-page <-> A/X/Y.
        if (srcIsZp && dst == MOS6502Registers.A) { Emit(builder, MOS6502Op.LdaZp, dst, src); return; }
        if (srcIsZp && dst == MOS6502Registers.X) { Emit(builder, MOS6502Op.LdxZp, dst, src); return; }
        if (srcIsZp && dst == MOS6502Registers.Y) { Emit(builder, MOS6502Op.LdyZp, dst, src); return; }
        if (src == MOS6502Registers.A && dstIsZp) { Emit(builder, MOS6502Op.StaZp, dst, src); return; }
        if (src == MOS6502Registers.X && dstIsZp) { Emit(builder, MOS6502Op.StxZp, dst, src); return; }
        if (src == MOS6502Registers.Y && dstIsZp) { Emit(builder, MOS6502Op.StyZp, dst, src); return; }

        // Impossible pairs — go through $A. See class comment about the latent
        // RA bug that lets these reach us with $A live.
        if (src == MOS6502Registers.X && dst == MOS6502Registers.Y)
        {
            Emit(builder, MOS6502Op.Txa, MOS6502Registers.A, src);
            Emit(builder, MOS6502Op.Tay, dst, MOS6502Registers.A);
            return;
        }
        if (src == MOS6502Registers.Y && dst == MOS6502Registers.X)
        {
            Emit(builder, MOS6502Op.Tya, MOS6502Registers.A, src);
            Emit(builder, MOS6502Op.Tax, dst, MOS6502Registers.A);
            return;
        }
        if (srcIsZp && dstIsZp)
        {
            Emit(builder, MOS6502Op.LdaZp, MOS6502Registers.A, src);
            Emit(builder, MOS6502Op.StaZp, dst, MOS6502Registers.A);
            return;
        }

        throw new NotImplementedException(
            $"MOS6502PseudoExpander: no expansion rule for pseudo.copy " +
            $"${MOS6502Registers.NameOf(dst)} = pseudo.copy ${MOS6502Registers.NameOf(src)}.");
    }

    private static void ExpandImmediateToReg(int dst, long value, MirBuilder builder)
    {
        switch (dst)
        {
            case MOS6502Registers.A:
                builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaImm),
                    new PhysicalReg(dst, IsDefinition: true),
                    new Immediate(value));
                return;
            case MOS6502Registers.X:
                builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdxImm),
                    new PhysicalReg(dst, IsDefinition: true),
                    new Immediate(value));
                return;
            case MOS6502Registers.Y:
                builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdyImm),
                    new PhysicalReg(dst, IsDefinition: true),
                    new Immediate(value));
                return;
        }

        if (IsZeroPage(dst))
        {
            // No direct "store immediate to zp" on the 6502 — it needs a GPR
            // scratch: `LD? #N ; ST? $zp`. We do NOT pick that GPR here; which
            // one is free depends on the surrounding physreg liveness, which is
            // a post-RA fact. Re-emit the copy as a 3-operand scratch form whose
            // third operand is a fresh vreg; RegisterScavengingPass fills it and
            // calls ExpandWithScratch to finish the lowering (plan §3.6).
            var scratch = builder.Function.CreateVirtualRegister(IRType.I8);
            builder.BuildInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
                new PhysicalReg(dst, IsDefinition: true),
                new Immediate(value),
                new VirtualReg(scratch, IsDefinition: true));
            return;
        }

        throw new NotImplementedException(
            $"MOS6502PseudoExpander: cannot materialise immediate {value} directly into " +
            $"${MOS6502Registers.NameOf(dst)}.");
    }

    // Lower a 3-operand scratch form `%dst = pseudo.copy %src using $scratch`
    // (operand[2]) into its final machine instructions. By the time the
    // scavenger calls us back, operand[2] is a concrete GPR physreg. Today the
    // only scratch form minted is immediate→zp; the switch is structured to
    // extend cleanly if other forms ever route through here.
    private static void ExpandWithScratch(MirInstruction copy, MirBuilder builder)
    {
        if (copy.Operands[0] is not PhysicalReg { IsDefinition: true } dst)
            throw new InvalidOperationException(
                "MOS6502PseudoExpander: scratch-form copy destination must be a physical register def.");
        if (copy.Operands[2] is not PhysicalReg scratch)
            throw new InvalidOperationException(
                "MOS6502PseudoExpander: scratch-form copy scratch operand was not assigned a " +
                "physical register (RegisterScavengingPass must run before final expansion).");

        switch (copy.Operands[1])
        {
            case Immediate imm when IsZeroPage(dst.Id):
                // immediate → zp, via the scavenged GPR: LD{scratch} #imm ; ST{scratch} $zp.
                builder.BuildInstruction(MOS6502Dialect.OpRef(ImmLoadOp(scratch.Id)),
                    new PhysicalReg(scratch.Id, IsDefinition: true),
                    new Immediate(imm.Value));
                builder.BuildInstruction(MOS6502Dialect.OpRef(StoreZpOp(scratch.Id)),
                    new PhysicalReg(dst.Id, IsDefinition: true),
                    new PhysicalReg(scratch.Id, IsDefinition: false));
                return;
            default:
                throw new InvalidOperationException(
                    $"MOS6502PseudoExpander: unsupported scratch-form pseudo.copy " +
                    $"(${MOS6502Registers.NameOf(dst.Id)} = copy {copy.Operands[1].GetType().Name}).");
        }
    }

    // The load-immediate opcode for a given GPR scratch register.
    private static MOS6502Op ImmLoadOp(int gpr) => gpr switch
    {
        MOS6502Registers.A => MOS6502Op.LdaImm,
        MOS6502Registers.X => MOS6502Op.LdxImm,
        MOS6502Registers.Y => MOS6502Op.LdyImm,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} cannot load an immediate."),
    };

    // The store-to-zero-page opcode for a given GPR scratch register.
    private static MOS6502Op StoreZpOp(int gpr) => gpr switch
    {
        MOS6502Registers.A => MOS6502Op.StaZp,
        MOS6502Registers.X => MOS6502Op.StxZp,
        MOS6502Registers.Y => MOS6502Op.StyZp,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} cannot store to zero page."),
    };

    private static void Emit(MirBuilder builder, MOS6502Op op, int dst, int src)
    {
        builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new PhysicalReg(src, IsDefinition: false));
    }

    private static bool IsZeroPage(int physReg) =>
        physReg >= MOS6502Registers.RC(0);
}
