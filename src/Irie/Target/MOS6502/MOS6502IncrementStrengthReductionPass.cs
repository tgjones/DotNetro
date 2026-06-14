using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// Target-private post-RA peephole that strength-reduces an in-place 8-bit
// `value ± 1` into a single `inc` / `dec` (memory or register) instead of the
// generic load / clc / adc / store sequence the legalizer + isel produce.
//
// Runs after MOS6502AddressingModeSelectorPass and before PseudoExpansionPass,
// so the surrounding loads / stores are still `pseudo.copy` and the add itself
// is the AMS-refined `mos6502.adc.zp` (inc) / `mos6502.sbc.zp` (dec).
//
// The matched window (all in one block, in program order) is:
//
//     $c = mos6502.clc               ; carry-in = 0  (for inc)
//     $a = pseudo.copy <loc>         ; load the value into A
//     $a, $c = mos6502.adc.zp $a, <const1>, $c
//     <loc> = pseudo.copy $a         ; store A back to the *same* location
//
// where:
//   - <loc> is a zero-page slot or the X / Y register (A has no inc/dec);
//   - the load source and the store destination are the same physreg (so the
//     add is genuinely in place — RA already tied the adc's A def/use, the
//     surrounding copies just move it to/from <loc>);
//   - <const1> is a zero-page slot defined earlier in the block by a
//     `pseudo.copy 1` (the materialised increment unit);
//   - the carry-out of the adc/sbc is dead (not read before being redefined),
//     i.e. the add is not a link in a wider multi-byte carry chain. A 16-bit
//     `inc lo / bne / inc hi` would need both bytes co-resident in zero page,
//     which RA does not currently guarantee, so the wide case stays on the
//     generic adc path (see the plan's Item 1 caveat).
//
// The dec mirror uses `sec` + `mos6502.sbc.zp <const1>`; 6502 `dec` sets no
// carry, so the same dead-carry-out requirement keeps the rewrite correct.
//
// On a match the adc/sbc becomes a single inc/dec, the load and store copies
// are dropped, and the now-dead clc/sec and constant-1 copy are removed if they
// have no other users in the block.
public sealed class MOS6502IncrementStrengthReductionPass : Irie.Passes.MirFunctionPass
{
    public override string Name => "MOS6502IncrementStrengthReduction";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
            RunOnBlock(block);
    }

    private static void RunOnBlock(MirBlock block)
    {
        var instrs = block.Instructions;

        // Find each adc.zp / sbc.zp and try to fold its surrounding window.
        for (var i = 0; i < instrs.Count; i++)
        {
            var add = instrs[i];
            if (add.Opcode.Dialect != MOS6502Dialect.Id) continue;

            var op = (MOS6502Op)add.Opcode.Code;
            var isInc = op == MOS6502Op.AdcZp;
            var isDec = op == MOS6502Op.SbcZp;
            if (!isInc && !isDec) continue;

            if (TryFold(block, i, isInc))
            {
                // The window shrank around index i; restart the scan of this
                // block to keep indices coherent (blocks are short).
                i = -1;
            }
        }
    }

    // adc.zp / sbc.zp operand layout (AdcInfo / SbcInfo):
    //   [0]=def $a (result), [1]=def $c (carry/borrow out),
    //   [2]=use $a (tied), [3]=use addend (zp), [4]=use $c (carry/borrow in).
    private const int ResultDef = 0;
    private const int CarryDef = 1;
    private const int AUse = 2;
    private const int AddendUse = 3;
    private const int CarryInUse = 4;

    private static bool TryFold(MirBlock block, int addIndex, bool isInc)
    {
        var instrs = block.Instructions;
        var add = instrs[addIndex];

        if (add.Operands.Length != 5) return false;
        if (add.Operands[ResultDef] is not PhysicalReg { Id: MOS6502Registers.A, IsDefinition: true }) return false;
        if (add.Operands[AUse] is not PhysicalReg { Id: MOS6502Registers.A, IsDefinition: false }) return false;
        if (add.Operands[CarryDef] is not PhysicalReg { Id: MOS6502Registers.C, IsDefinition: true }) return false;
        if (add.Operands[CarryInUse] is not PhysicalReg { Id: MOS6502Registers.C, IsDefinition: false }) return false;
        if (add.Operands[AddendUse] is not PhysicalReg { IsDefinition: false } addend) return false;
        if (!IsZeroPage(addend.Id)) return false;

        // The load: the instruction immediately before the add must be
        // `$a = pseudo.copy <loc>` with <loc> an inc/dec-able location.
        var loadIndex = addIndex - 1;
        if (loadIndex < 0) return false;
        var load = instrs[loadIndex];
        if (!IsCopy(load, out var loadDst, out var loadSrc)) return false;
        if (loadDst is not PhysicalReg { Id: MOS6502Registers.A }) return false;
        if (loadSrc is not PhysicalReg loc) return false;
        if (!IsIncDecLocation(loc.Id)) return false;

        // The store: the instruction immediately after the add must be
        // `<loc> = pseudo.copy $a`, writing back to the same location.
        var storeIndex = addIndex + 1;
        if (storeIndex >= instrs.Count) return false;
        var store = instrs[storeIndex];
        if (!IsCopy(store, out var storeDst, out var storeSrc)) return false;
        if (storeSrc is not PhysicalReg { Id: MOS6502Registers.A }) return false;
        if (storeDst is not PhysicalReg storeLoc || storeLoc.Id != loc.Id) return false;

        // The addend must be a constant `1` materialised earlier in the block.
        if (!IsConstantOne(block, addIndex, addend.Id)) return false;

        // The carry-in must come from a fresh clc (inc) / sec (dec) — i.e. this
        // add is not a link in a multi-byte carry chain — and the carry-out
        // must be dead after the store. Both guard against folding one byte of
        // a wider add into an isolated inc/dec.
        if (!CarryOutIsDead(block, storeIndex)) return false;

        // Build the replacement inc/dec.
        var replacement = BuildIncDec(block, loc.Id, isInc);

        // Replace the add with the inc/dec; drop the load and store copies.
        instrs[addIndex] = replacement;
        instrs.RemoveAt(storeIndex); // after addIndex
        instrs.RemoveAt(loadIndex);  // before addIndex (now addIndex-1 shifts)

        // Clean up the now-dead carry-in flag op and the constant-1 copy if
        // nothing else in the block uses them.
        RemoveDeadCarrySources(block);
        RemoveDeadConstantSource(block, addend.Id);

        return true;
    }

    private static MirInstruction BuildIncDec(MirBlock block, int locId, bool isInc)
    {
        if (locId == MOS6502Registers.X)
        {
            // inx / dex — implied; model the X read+write as implicit operands
            // so later liveness reasoning sees the dependency.
            return new MirInstruction(
                MOS6502Dialect.OpRef(isInc ? MOS6502Op.Inx : MOS6502Op.Dex),
                [
                    new PhysicalReg(MOS6502Registers.X, IsDefinition: true, IsImplicit: true),
                    new PhysicalReg(MOS6502Registers.X, IsDefinition: false, IsImplicit: true),
                ])
            { Parent = block };
        }

        if (locId == MOS6502Registers.Y)
        {
            return new MirInstruction(
                MOS6502Dialect.OpRef(isInc ? MOS6502Op.Iny : MOS6502Op.Dey),
                [
                    new PhysicalReg(MOS6502Registers.Y, IsDefinition: true, IsImplicit: true),
                    new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true),
                ])
            { Parent = block };
        }

        // inc.zp / dec.zp — read-modify-write in place: def[0]=zp, use[0]=zp.
        return new MirInstruction(
            MOS6502Dialect.OpRef(isInc ? MOS6502Op.IncZp : MOS6502Op.DecZp),
            [
                new PhysicalReg(locId, IsDefinition: true),
                new PhysicalReg(locId, IsDefinition: false),
            ])
        { Parent = block };
    }

    // A zero-page slot or the X / Y register. The accumulator is excluded — the
    // 6502 has no inc/dec of A.
    private static bool IsIncDecLocation(int physReg)
        => physReg == MOS6502Registers.X
        || physReg == MOS6502Registers.Y
        || IsZeroPage(physReg);

    private static bool IsZeroPage(int physReg) => physReg >= MOS6502Registers.RC(0);

    private static bool IsCopy(MirInstruction instr, out MirOperand dst, out MirOperand src)
    {
        dst = null!;
        src = null!;
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 2) return false;
        if (instr.Operands[0] is not { } d || instr.Operands[1] is not { } s) return false;
        dst = d;
        src = s;
        return true;
    }

    // True if the zero-page slot `addendId` is the destination of an earlier
    // `<zp> = pseudo.copy 1` in this block, with no intervening redefinition.
    private static bool IsConstantOne(MirBlock block, int addIndex, int addendId)
    {
        var instrs = block.Instructions;
        for (var i = addIndex - 1; i >= 0; i--)
        {
            var instr = instrs[i];
            if (DefinesPhysReg(instr, addendId, out var defIsCopyOfOne))
                return defIsCopyOfOne;
        }
        return false;
    }

    // Whether `instr` writes physreg `id`. Sets `isCopyOfImmediateOne` to true
    // only when the writing instruction is exactly `$id = pseudo.copy 1`.
    private static bool DefinesPhysReg(MirInstruction instr, int id, out bool isCopyOfImmediateOne)
    {
        isCopyOfImmediateOne = false;
        foreach (var operand in instr.Operands)
        {
            if (operand is PhysicalReg { IsDefinition: true } def && def.Id == id)
            {
                if (instr.Opcode.Dialect == PseudoDialect.Id
                    && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy
                    && instr.Operands.Length == 2
                    && instr.Operands[1] is Immediate { Value: 1 })
                {
                    isCopyOfImmediateOne = true;
                }
                return true;
            }
        }
        return false;
    }

    // The carry flag ($c) is dead at the point after `fromIndex` if, scanning
    // forward in the block, it is redefined before it is used. Reaching the end
    // of the block (or a terminator) without a use also counts as dead — block
    // live-outs would surface a $c use as an explicit operand on a branch.
    private static bool CarryOutIsDead(MirBlock block, int fromIndex)
    {
        var instrs = block.Instructions;
        for (var i = fromIndex + 1; i < instrs.Count; i++)
        {
            var instr = instrs[i];

            // Within a single instruction the use is read before the def is
            // written (e.g. adc.zp has both a $c use (carry-in) and a $c def
            // (carry-out)), so check for any use of $c first: a use keeps the
            // carry live regardless of a co-located def.
            var usesCarry = false;
            var definesCarry = false;
            foreach (var operand in instr.Operands)
            {
                if (operand is not PhysicalReg phys || phys.Id != MOS6502Registers.C) continue;
                if (phys.IsDefinition) definesCarry = true;
                else usesCarry = true;
            }

            if (usesCarry) return false;       // read before any redefinition → live
            if (definesCarry) return true;     // redefined without a read → dead
        }
        return true;
    }

    // Remove the clc/sec that fed the (now-removed) add's carry-in if it has no
    // remaining users. The carry-in source is the most recent clc/sec before
    // the fold site; after removing the add, nothing reads its $c def.
    private static void RemoveDeadCarrySources(MirBlock block)
    {
        var instrs = block.Instructions;
        for (var i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (instr.Opcode.Dialect != MOS6502Dialect.Id) continue;
            var op = (MOS6502Op)instr.Opcode.Code;
            if (op != MOS6502Op.Clc && op != MOS6502Op.Sec) continue;
            if (PhysRegHasLaterUse(block, i, MOS6502Registers.C)) continue;
            instrs.RemoveAt(i);
            i--;
        }
    }

    // Remove the `$zp = pseudo.copy 1` that materialised the increment unit if
    // the zero-page slot has no remaining users.
    private static void RemoveDeadConstantSource(MirBlock block, int addendId)
    {
        var instrs = block.Instructions;
        for (var i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
            if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
            if (instr.Operands.Length != 2) continue;
            if (instr.Operands[0] is not PhysicalReg { IsDefinition: true } def || def.Id != addendId) continue;
            if (instr.Operands[1] is not Immediate) continue;
            if (PhysRegHasLaterUse(block, i, addendId)) continue;
            instrs.RemoveAt(i);
            i--;
        }
    }

    // Whether physreg `id` is read by any instruction after `defIndex` before
    // being redefined. Conservative: a redefinition (without an intervening
    // read) ends the search with "no later use".
    private static bool PhysRegHasLaterUse(MirBlock block, int defIndex, int id)
    {
        var instrs = block.Instructions;
        for (var i = defIndex + 1; i < instrs.Count; i++)
        {
            var instr = instrs[i];

            // Use-before-def within a single instruction (an adc.zp both uses
            // and defines $c): a use keeps the value live regardless of a
            // co-located def.
            var used = false;
            var defined = false;
            foreach (var operand in instr.Operands)
            {
                if (operand is not PhysicalReg phys || phys.Id != id) continue;
                if (phys.IsDefinition) defined = true;
                else used = true;
            }

            if (used) return true;
            if (defined) return false; // redefined before any use
        }
        return false;
    }
}
