using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private late-optimization pass — the llvm-mos `mos-late-opt`
// (MOSLateOptimization::lowerCmpZeros) analogue. Runs AFTER PseudoExpansion so
// it sees the final expanded loads/transfers (lda.zp / ldx.zp / ldy.zp / tax /
// tay / txa / tya …) with their N/Z side-effects.
//
// A `cmp $r, #0` (pre-AMS `mos6502.cmp` or post-AMS `mos6502.cmp.imm`) is
// redundant when `$r` was just produced by a flag-setting load or transfer with
// no intervening N/Z clobber: the comparison against zero would set exactly the
// N/Z that the producer already set. We erase the compare and tag the producer
// with an implicit N/Z def so the downstream branch's implicit flag use still
// has a reaching def (mirrors llvm-mos adding the implicit NZ def operand).
//
// Conservative, mirrors llvm-mos: scan backward from the compare within the
// same block; stop at the first instruction that clobbers N/Z. If that
// instruction is the producer of `$r`, fold; otherwise leave the compare.
public sealed class MOS6502LateOptimizationPass : MirFunctionPass
{
    public override string Name => "MOS6502LateOptimization";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (!IsCmpAgainstZero(instr, out var comparedReg)) continue;

                // Scan backward for the producer of comparedReg. Stop at the
                // first N/Z clobber (mirrors llvm-mos's `modifiesRegister(NZ)`
                // bail-out): if that clobber IS the producer we fold, otherwise
                // the existing N/Z does not reflect comparedReg and we keep the
                // compare.
                for (var j = i - 1; j >= 0; j--)
                {
                    var prev = block.Instructions[j];
                    if (DefinesNZForRegister(prev, comparedReg))
                    {
                        // The producer already sets N/Z for comparedReg; the
                        // compare is redundant. Tag the producer with an
                        // explicit implicit-def of $n/$z so the branch's
                        // implicit flag use still resolves, then drop the cmp.
                        block.Instructions[j] = WithImplicitNZDefs(prev, block);
                        block.Instructions.RemoveAt(i);
                        i--;
                        break;
                    }

                    if (ClobbersNZ(prev)) break;
                }
            }
        }
    }

    // Matches `mos6502.cmp $r, #0` and its AMS-refined `mos6502.cmp.imm $r, #0`.
    // Explicit operands are use[0]=a (the register) and use[1]=b; the implicit
    // $n/$z/$c defs follow. Only an immediate-zero RHS qualifies.
    private static bool IsCmpAgainstZero(MirInstruction instr, out int comparedReg)
    {
        comparedReg = -1;
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;

        var op = (MOS6502Op)instr.Opcode.Code;
        if (op is not (MOS6502Op.Cmp or MOS6502Op.CmpImm)) return false;

        if (instr.Operands.Length < 2) return false;
        if (instr.Operands[0] is not PhysicalReg a) return false;
        if (instr.Operands[1] is not Immediate { Value: 0 }) return false;

        comparedReg = a.Id;
        return true;
    }

    // True if `instr` is a flag-setting load/transfer whose result is `reg` —
    // i.e. it already leaves N/Z reflecting `reg`. The dialect models these via
    // ImplicitDefs of $n/$z (see MOS6502Dialect.NZFlagSettingInfo); here we also
    // require the instruction to actually define `reg` (its first def operand).
    private static bool DefinesNZForRegister(MirInstruction instr, int reg)
    {
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;
        if (!SetsNZ(instr)) return false;

        // The produced register is the (single) explicit def — the first
        // operand for these loads/transfers.
        return instr.Operands.Length > 0
            && instr.Operands[0] is PhysicalReg { IsDefinition: true } def
            && def.Id == reg;
    }

    // An instruction clobbers N/Z if it (re)defines either flag. We read this
    // from the dialect's ImplicitDefs plus any explicit flag defs in the operand
    // array. Branches, jumps and pure stores don't touch N/Z, so the scan walks
    // past them to reach the producer (as in llvm-mos).
    private static bool ClobbersNZ(MirInstruction instr)
    {
        foreach (var operand in instr.Operands)
        {
            if (operand is PhysicalReg { IsDefinition: true } p &&
                (p.Id == MOS6502Registers.N || p.Id == MOS6502Registers.Z))
            {
                return true;
            }
        }

        return SetsNZ(instr);
    }

    // True if the dialect declares this opcode as defining N/Z implicitly.
    private static bool SetsNZ(MirInstruction instr)
    {
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;
        var dialect = DialectRegistry.ById(MOS6502Dialect.Id);
        var info = dialect.GetInstructionInfo(instr.Opcode.Code);
        var defs = info.ImplicitDefs;
        if (defs == null) return false;
        foreach (var d in defs)
            if (d == MOS6502Registers.N || d == MOS6502Registers.Z) return true;
        return false;
    }

    // Append implicit-def $n / $z operands to a producer that didn't already
    // carry them, so the now-flagless branch's implicit flag use has a reaching
    // def in the MIR (mirrors llvm-mos's `addOperand(NZ def)`). Idempotent.
    private static MirInstruction WithImplicitNZDefs(MirInstruction instr, MirBlock block)
    {
        bool HasDef(int reg) => Array.Exists(instr.Operands,
            o => o is PhysicalReg { IsDefinition: true } p && p.Id == reg);

        var toAdd = new List<MirOperand>();
        if (!HasDef(MOS6502Registers.N))
            toAdd.Add(new PhysicalReg(MOS6502Registers.N, IsDefinition: true, IsImplicit: true));
        if (!HasDef(MOS6502Registers.Z))
            toAdd.Add(new PhysicalReg(MOS6502Registers.Z, IsDefinition: true, IsImplicit: true));

        if (toAdd.Count == 0) return instr;

        return new MirInstruction(instr.Opcode, [.. instr.Operands, .. toAdd])
        {
            Parent = block,
        };
    }
}
