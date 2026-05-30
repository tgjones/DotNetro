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
// in one instruction ("impossible pairs" in unified-IR plan §5.6) go through
// $A using two instructions — `tya;tax` for $X = pseudo.copy $Y, and
// `lda.zp;sta.zp` for RC → RC. The latter clobbers $A whether $A is live or
// not; the v2 register allocator currently doesn't avoid emitting RC → RC
// copies in live-$A windows, so the resulting code can be semantically wrong.
// That's a known RA quality bug to be addressed in a later step — this
// expander faithfully implements what the plan calls for.
public sealed class MOS6502PseudoExpander : Irie.Target.PseudoExpander
{
    public override void Expand(MirInstruction copy, MirBuilder builder)
    {
        if (copy.Operands.Length != 2)
            throw new InvalidOperationException(
                $"MOS6502PseudoExpander: pseudo.copy must have 2 operands, got {copy.Operands.Length}.");

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
        var op = dst switch
        {
            MOS6502Registers.A => MOS6502Op.LdaImm,
            MOS6502Registers.X => MOS6502Op.LdxImm,
            MOS6502Registers.Y => MOS6502Op.LdyImm,
            _ => throw new NotImplementedException(
                $"MOS6502PseudoExpander: cannot materialise immediate {value} directly into " +
                $"${MOS6502Registers.NameOf(dst)}; only A/X/Y supported."),
        };

        builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new Immediate(value));
    }

    private static void Emit(MirBuilder builder, MOS6502Op op, int dst, int src)
    {
        builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new PhysicalReg(src, IsDefinition: false));
    }

    private static bool IsZeroPage(int physReg) =>
        physReg >= MOS6502Registers.RC(0);
}
