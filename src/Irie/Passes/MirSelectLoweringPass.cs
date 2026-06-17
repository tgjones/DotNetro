using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Mir;

namespace Irie.Passes;

// Lowers materialized `arith.select` (a value select, i.e. result width > 1)
// into a CFG diamond, mirroring llvm-mos MOSLowerSelect. Runs after the
// legalizer and before instruction selection.
//
// For `%r = arith.select %c, %a, %b` this produces:
//
//   block:     <instructions before the select>
//              cf.cond_br %c, trueBB(), falseBB()
//   trueBB:    cf.br sinkBB(%a)
//   falseBB:   cf.br sinkBB(%b)
//   sinkBB(%r): <the rest of `block`, with %r now a block parameter>
//
// The select's def vreg is reused as the sink block's parameter, so downstream
// uses are unchanged; PhiEliminationPass later lowers the block argument to
// per-edge copies.
//
// Two optimizations match llvm-mos:
//   * same-test merge — all value selects in a block sharing the same condition
//     are collapsed into ONE diamond (one parameter / arm-arg per select), so a
//     narrowed multi-byte select (legalized to N i8 selects sharing %c) emits a
//     single diamond with all bytes set per arm.
//   * arm sinking — a single-use value computation that feeds exactly one arm is
//     moved out of `block` into that arm's block, so it runs only on the taken
//     path. This also keeps the condition's `arith.cmpi` adjacent to the
//     `cf.cond_br`, which the instruction selector's cmpi+cond_br fusion requires.
//
// An i1 (width-1) select is NOT lowered here: it only ever arises from the
// legalizer's wide-compare lexicographic lowering and feeds a `cf.cond_br`, where
// the instruction selector re-fuses it into a CMP+branch ladder. Lowering it to a
// diamond would needlessly materialize a boolean byte.
public sealed class MirSelectLoweringPass : MirFunctionPass
{
    public override string Name => "MirSelectLowering";

    public override void Run(MirFunction function)
    {
        var worklist = new Queue<MirBlock>(function.Blocks);
        while (worklist.Count > 0)
        {
            var block = worklist.Dequeue();
            var seed = FindFirstValueSelect(function, block);
            if (seed is null) continue;

            var sink = LowerSelectGroup(function, block, seed);
            // The sink block holds everything that followed the select group and
            // may contain further selects (a different condition, or a select
            // that consumed an earlier select's result); re-process it.
            worklist.Enqueue(sink);
        }
    }

    // The first `arith.select` in `block` whose result is a value (width > 1).
    private static MirInstruction? FindFirstValueSelect(MirFunction function, MirBlock block)
    {
        foreach (var instr in block.Instructions)
            if (IsValueSelect(function, instr))
                return instr;
        return null;
    }

    private static bool IsValueSelect(MirFunction function, MirInstruction instr)
    {
        if (instr.Opcode.Dialect != ArithDialect.Id || (ArithOp)instr.Opcode.Code != ArithOp.Select)
            return false;
        if (instr.Operands.Length != 4 || instr.Operands[0] is not VirtualReg def || !def.IsDefinition)
            return false;
        return function.GetVRegAnnotation(def.Id) is TypedVReg { Type.SizeInBits: > 1 };
    }

    private static int ConditionOf(MirInstruction select) =>
        ((VirtualReg)select.Operands[1]).Id;

    // Build the diamond for the group of value selects in `block` sharing
    // `seed`'s condition. Returns the new sink block.
    private static MirBlock LowerSelectGroup(MirFunction function, MirBlock block, MirInstruction seed)
    {
        var builder = new MirBuilder(function);
        var cond = ConditionOf(seed);
        var firstIdx = block.Instructions.IndexOf(seed);

        // Partition the tail (everything from the seed onward) into merged
        // selects (those sharing `cond`) and the instructions that move to sink.
        var tail = block.Instructions.GetRange(firstIdx, block.Instructions.Count - firstIdx);

        var selectDefs = new List<int>();   // becomes the sink block's parameters
        var trueArgs   = new List<MirOperand>();
        var falseArgs  = new List<MirOperand>();
        var sinkInstrs = new List<MirInstruction>();

        foreach (var instr in tail)
        {
            if (IsValueSelect(function, instr) && ConditionOf(instr) == cond)
            {
                selectDefs.Add(((VirtualReg)instr.Operands[0]).Id);
                trueArgs.Add(new VirtualReg(((VirtualReg)instr.Operands[2]).Id, IsDefinition: false));
                falseArgs.Add(new VirtualReg(((VirtualReg)instr.Operands[3]).Id, IsDefinition: false));
            }
            else
            {
                sinkInstrs.Add(instr);
            }
        }

        // Detach the tail from `block`.
        block.Instructions.RemoveRange(firstIdx, block.Instructions.Count - firstIdx);

        // Create the three diamond blocks, positioned right after `block`.
        var trueBB = function.CreateBlock();
        var falseBB = function.CreateBlock();
        var sinkBB = function.CreateBlock();
        RepositionAfter(function, block, [trueBB, falseBB, sinkBB]);

        // Re-parent the sink instructions into the sink block.
        foreach (var instr in sinkInstrs)
        {
            instr.Parent = sinkBB;
            sinkBB.Instructions.Add(instr);
        }
        sinkBB.Parameters.AddRange(selectDefs);

        // block: cf.cond_br %c, trueBB(), falseBB()
        builder.SetInsertionPointAtEnd(block);
        builder.BuildInstruction(
            CfDialect.OpRef(CfOp.CondBr),
            new VirtualReg(cond, IsDefinition: false),
            new BlockTarget(trueBB, []),
            new BlockTarget(falseBB, []));

        // trueBB / falseBB: cf.br sinkBB(<args>)
        var trueBr = trueBB.AddInstruction(
            CfDialect.OpRef(CfOp.Br), new BlockTarget(sinkBB, trueArgs.ToArray()));
        var falseBr = falseBB.AddInstruction(
            CfDialect.OpRef(CfOp.Br), new BlockTarget(sinkBB, falseArgs.ToArray()));

        // Arm sinking: now that the arm branches carry the arm args as real uses,
        // move single-use arm-value computations out of `block` into the arm that
        // uses them (before that arm's terminator). This runs the computation only
        // on the taken path and, crucially, removes it from between the condition's
        // cmpi and the cond_br so the selector's cmpi+cond_br fusion still fires.
        SinkArmValues(function, block, trueBB, trueBr, trueArgs);
        SinkArmValues(function, block, falseBB, falseBr, falseArgs);

        return sinkBB;
    }

    // Move the def of each arm argument into `armBlock` (immediately before its
    // terminator `armTerm`) when that def lives in `block`, is side-effect-free,
    // reads no physical register, and is used only by this arm (single use — the
    // arm branch's argument is now its only use). The single-use check excludes
    // values the condition also consumes (e.g. a min/max operand fed to both the
    // cmpi and an arm), so those correctly stay in `block`.
    private static void SinkArmValues(
        MirFunction function, MirBlock block, MirBlock armBlock, MirInstruction armTerm, List<MirOperand> args)
    {
        foreach (var arg in args)
        {
            if (arg is not VirtualReg v) continue;
            var def = function.GetDefinition(v.Id);
            if (def is null || def.Parent != block) continue;

            var dialect = DialectRegistry.ById(def.Opcode.Dialect);
            if (!dialect.IsSideEffectFree(def.Opcode.Code)) continue;
            if (function.GetUseCount(v.Id) != 1) continue;
            // A def reading a physical register (e.g. an ABI copy from $a) cannot
            // be sunk past the cmpi, whose funnel may clobber that register.
            if (ReadsPhysicalRegister(def)) continue;

            block.Instructions.Remove(def);
            def.Parent = armBlock;
            var termIdx = armBlock.Instructions.IndexOf(armTerm);
            armBlock.Instructions.Insert(termIdx, def);
        }
    }

    private static bool ReadsPhysicalRegister(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is PhysicalReg p && !p.IsDefinition)
                return true;
        return false;
    }

    // Move `newBlocks` (just appended to the end of function.Blocks by
    // CreateBlock) to sit immediately after `anchor`, preserving their order.
    private static void RepositionAfter(MirFunction function, MirBlock anchor, MirBlock[] newBlocks)
    {
        foreach (var b in newBlocks)
            function.Blocks.Remove(b);
        var anchorIdx = function.Blocks.IndexOf(anchor);
        function.Blocks.InsertRange(anchorIdx + 1, newBlocks);
    }
}
