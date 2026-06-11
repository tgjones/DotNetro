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
// Copy scratch (plan §3.6): a copy that needs a temporary register/slot to
// lower is NOT lowered eagerly through a hardcoded $a. Instead it is re-emitted
// as a 3-operand "scratch form" `pseudo.copy %dst, %src, %scratch` whose third
// operand is a FRESH virtual register. The post-RA RegisterScavengingPass then
// assigns that scratch vreg a location that is DEAD at the copy's point (using
// the candidate set the target advertises per copy shape) and calls this
// expander back on the now-physreg-only 3-operand form, which lowers it to the
// final two machine instructions. This is the single, uniform mechanism for all
// copy scratch — it lets scratch be $x/$y/zp when those are free, instead of
// forcing $a free for every such copy (the old GetPseudoCopyScratchClobbers
// approach, now deleted).
//
// Three copy shapes need scratch, all routed through that one mechanism:
//   * immediate → zp        — needs a GPR: `LD? #imm ; ST? $zp`.
//   * zp → zp               — needs a GPR: `LD? $src ; ST? $dst`.
//   * $x ↔ $y               — needs $a (`T?A ; TA?`) OR a dead zp slot
//                             (`ST? $tmp ; LD? $tmp`); the scavenger picks
//                             whichever is free, so a live $a is never trashed.
// Because the scavenger preserves any live value, MOS6502ParallelCopyPass no
// longer has to evacuate $a — it is now pure parallel-copy sequentialization.
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

        // Impossible pairs ($x↔$y, zp→zp) need a scratch register/slot. WHICH is
        // free is a post-RA fact, so — exactly like immediate→zp — re-emit as a
        // 3-operand scratch form and let RegisterScavengingPass pick the scratch
        // (a dead GPR, or for $x↔$y a dead zero-page slot) with real liveness.
        var isXyPair =
            (src == MOS6502Registers.X && dst == MOS6502Registers.Y) ||
            (src == MOS6502Registers.Y && dst == MOS6502Registers.X);
        if (isXyPair || (srcIsZp && dstIsZp))
        {
            EmitScratchForm(dst, new PhysicalReg(src, IsDefinition: false), builder);
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
            // scratch: `LD? #N ; ST? $zp`. Re-emit as a scratch form (see below).
            EmitScratchForm(dst, new Immediate(value), builder);
            return;
        }

        throw new NotImplementedException(
            $"MOS6502PseudoExpander: cannot materialise immediate {value} directly into " +
            $"${MOS6502Registers.NameOf(dst)}.");
    }

    // Re-emit a scratch-needing copy as a 3-operand "scratch form"
    // `pseudo.copy %dst, <src>, %scratch` whose third operand is a FRESH virtual
    // register. WHICH location is free for the scratch depends on the surrounding
    // physreg liveness — a post-RA fact — so we do not pick it here.
    // RegisterScavengingPass fills the scratch vreg (consulting the target's
    // per-shape candidate set) and calls ExpandWithScratch to finish lowering.
    private static void EmitScratchForm(int dst, MirOperand src, MirBuilder builder)
    {
        var scratch = builder.Function.CreateVirtualRegister(IRType.I8);
        builder.BuildInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(dst, IsDefinition: true),
            src,
            new VirtualReg(scratch, IsDefinition: true));
    }

    // Lower a 3-operand scratch form `%dst = pseudo.copy %src using $scratch`
    // (operand[2]) into its final two machine instructions. By the time the
    // scavenger calls us back, operand[2] is a concrete physreg (a GPR for the
    // immediate→zp / zp→zp forms; a GPR=$a OR a zero-page slot for $x↔$y).
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
                EmitImm(builder, ImmLoadOp(scratch.Id), scratch.Id, imm.Value);
                Emit(builder, StoreZpOp(scratch.Id), dst.Id, scratch.Id);
                return;

            case PhysicalReg src when IsZeroPage(src.Id) && IsZeroPage(dst.Id):
                // zp → zp, via the scavenged GPR: LD{scratch} $src ; ST{scratch} $dst.
                Emit(builder, LoadZpOp(scratch.Id), scratch.Id, src.Id);
                Emit(builder, StoreZpOp(scratch.Id), dst.Id, scratch.Id);
                return;

            case PhysicalReg src when IsXyPair(src.Id, dst.Id) && !IsZeroPage(scratch.Id):
                // $x↔$y via $a: T{src}A ; TA{dst}. (The only GPR free here is $a,
                // since $x/$y are the copy's own ends.)
                Emit(builder, TransferToAOp(src.Id), MOS6502Registers.A, src.Id);
                Emit(builder, TransferFromAOp(dst.Id), dst.Id, MOS6502Registers.A);
                return;

            case PhysicalReg src when IsXyPair(src.Id, dst.Id):
                // $x↔$y via a dead zp slot: ST{src} $scratch ; LD{dst} $scratch.
                // Touches no GPR, so a live $a survives untouched.
                Emit(builder, StoreZpOp(src.Id), scratch.Id, src.Id);
                Emit(builder, LoadZpOp(dst.Id), dst.Id, scratch.Id);
                return;

            default:
                throw new InvalidOperationException(
                    $"MOS6502PseudoExpander: unsupported scratch-form pseudo.copy " +
                    $"(${MOS6502Registers.NameOf(dst.Id)} = copy {copy.Operands[1].GetType().Name}).");
        }
    }

    private static bool IsXyPair(int a, int b) =>
        (a == MOS6502Registers.X && b == MOS6502Registers.Y) ||
        (a == MOS6502Registers.Y && b == MOS6502Registers.X);

    // The load-immediate opcode for a given GPR scratch register.
    private static MOS6502Op ImmLoadOp(int gpr) => gpr switch
    {
        MOS6502Registers.A => MOS6502Op.LdaImm,
        MOS6502Registers.X => MOS6502Op.LdxImm,
        MOS6502Registers.Y => MOS6502Op.LdyImm,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} cannot load an immediate."),
    };

    // The load-from-zero-page opcode for a given GPR.
    private static MOS6502Op LoadZpOp(int gpr) => gpr switch
    {
        MOS6502Registers.A => MOS6502Op.LdaZp,
        MOS6502Registers.X => MOS6502Op.LdxZp,
        MOS6502Registers.Y => MOS6502Op.LdyZp,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} cannot load from zero page."),
    };

    // The store-to-zero-page opcode for a given GPR.
    private static MOS6502Op StoreZpOp(int gpr) => gpr switch
    {
        MOS6502Registers.A => MOS6502Op.StaZp,
        MOS6502Registers.X => MOS6502Op.StxZp,
        MOS6502Registers.Y => MOS6502Op.StyZp,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} cannot store to zero page."),
    };

    // Transfer $x/$y → $a (Txa / Tya).
    private static MOS6502Op TransferToAOp(int gpr) => gpr switch
    {
        MOS6502Registers.X => MOS6502Op.Txa,
        MOS6502Registers.Y => MOS6502Op.Tya,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} has no transfer-to-$a op."),
    };

    // Transfer $a → $x/$y (Tax / Tay).
    private static MOS6502Op TransferFromAOp(int gpr) => gpr switch
    {
        MOS6502Registers.X => MOS6502Op.Tax,
        MOS6502Registers.Y => MOS6502Op.Tay,
        _ => throw new InvalidOperationException(
            $"MOS6502PseudoExpander: ${MOS6502Registers.NameOf(gpr)} has no transfer-from-$a op."),
    };

    private static void Emit(MirBuilder builder, MOS6502Op op, int dst, int src)
    {
        builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new PhysicalReg(src, IsDefinition: false));
    }

    // Load-immediate variant: `op $dst, #imm` (one physreg def + immediate use).
    private static void EmitImm(MirBuilder builder, MOS6502Op op, int dst, long imm)
    {
        builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new Immediate(imm));
    }

    private static bool IsZeroPage(int physReg) =>
        physReg >= MOS6502Registers.RC(0);
}
