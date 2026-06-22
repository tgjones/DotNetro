using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Inserts a class-crossing legalization copy wherever a use operand's REQUIRED
// register class is disjoint from the SOURCE vreg's annotated (def) class — i.e.
// where the value can never legally sit in any register the operand accepts.
//
// This is the llvm-mos ISel behaviour: an `$a`-class result (e.g. from `adc` or
// a load) that must become an `adc` addend (an `imag8`/zero-page operand) is
// COPY'd into a fresh `imag8` vreg at selection time —
//   %23:ac, dead %11:cc = ASL %23
//   %10:imag8 = COPY %23          ; class-crossing COPY, a SEPARATE vreg
//   %8:ac, ...              = ADCImag8 %8, %10, %21
// so every vreg has ONE consistent class and the allocator never sees a vreg
// whose allowed set is `Ac ∩ Imag8 = ∅` (the throw in
// RegisterAllocationSupport.ComputeAllowedColours). This pass reconstructs that
// missing copy generically: for every use operand that is class-disjoint from
// its source, mint `%tmp = pseudo.copy %src` in the OPERAND'S required class and
// rewrite the operand to `%tmp` (matching `%10:imag8 = COPY %23`).
//
// Pipeline slot: after InstructionSelectorPass (operands now carry concrete
// target opcodes with declared OperandClasses, and source vregs carry
// ClassedVReg annotations) and before PhiEliminationPass / TwoAddressInstruction
// / RegisterAllocator (so the copies it inserts and the allocator both see the
// legalized, single-class vregs). Right after isel is the cleanest slot — the
// later copy-inserting passes do not change a vreg's class, so order among them
// is otherwise immaterial.
//
// NO-OP property: while instruction selection still relocates every forced-`$a`
// result into the flexible class (`Anyi8` ⊇ `Imag8`), no use is class-disjoint
// and this pass inserts ZERO copies on every existing function. It is wired in
// unconditionally precisely because it is provably a no-op until those funnels
// are removed (greedy-RA plan Stage 4b).
public sealed class OperandLegalizationPass : MirFunctionPass
{
    private readonly TargetRegisterInfo _registerInfo;

    public OperandLegalizationPass(TargetRegisterInfo registerInfo)
    {
        _registerInfo = registerInfo;
    }

    public override string Name => "OperandLegalization";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
            ProcessBlock(block, function);
    }

    private void ProcessBlock(MirBlock block, MirFunction function)
    {
        var i = 0;
        while (i < block.Instructions.Count)
        {
            var instr = block.Instructions[i];
            var inserted = ProcessInstruction(block, i, instr, function);
            // Skip past any copies inserted before this instruction (they need no
            // further legalization — a pseudo.copy declares no operand classes),
            // then advance past the instruction itself.
            i += inserted + 1;
        }
    }

    // Returns the number of pseudo.copy instructions inserted before instrIdx.
    private int ProcessInstruction(
        MirBlock block, int instrIdx, MirInstruction instr, MirFunction function)
    {
        var info = DialectRegistry.ById(instr.Opcode.Dialect)
            .GetInstructionInfo(instr.Opcode.Code);
        var classes = info.OperandClasses;
        if (classes == null) return 0;

        var operands = instr.Operands;
        var inserted = 0;
        for (var opIdx = 0; opIdx < operands.Length && opIdx < classes.Length; opIdx++)
        {
            var requiredClass = classes[opIdx];
            if (requiredClass == 0) continue;
            if (operands[opIdx] is not VirtualReg v || v.IsDefinition) continue;

            // The source vreg's def class (its uniform ClassedVReg annotation).
            if (function.GetVRegAnnotation(v.Id) is not ClassedVReg defClass)
                continue;

            // Same class can never be disjoint; this also fast-paths every
            // flag-class operand (a Cc value feeds a Cc operand) without ever
            // querying GetAllocatableRegisters for a flag class.
            if (defClass.ClassId == requiredClass) continue;

            if (!AreClassesDisjoint(defClass.ClassId, requiredClass)) continue;

            // Disjoint: the value cannot live in any register the operand
            // accepts. Mint a fresh vreg in the OPERAND'S required class and copy
            // the source into it, then point the operand at the copy.
            var requiredClassName =
                _registerInfo.GetRegisterClassName(requiredClass) ?? $"class{requiredClass}";
            var temp = function.CreateVirtualRegisterInClass(requiredClass, requiredClassName);

            block.InsertInstruction(
                instrIdx + inserted,
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(temp, IsDefinition: true),
                new VirtualReg(v.Id, IsDefinition: false));
            inserted++;

            operands[opIdx] = new VirtualReg(temp, IsDefinition: false);
        }

        return inserted;
    }

    // Two classes are disjoint when neither's allocatable register set contains
    // any register of the other's — i.e. no physreg satisfies both. Flag classes
    // whose allocatable set is not modelled (GetAllocatableRegisters throws) are
    // treated as "not disjoint" so we never touch them; the same-class fast path
    // above already covers the flag-feeds-matching-flag case, so reaching here
    // with an unmodelled class is conservatively a no-op.
    private bool AreClassesDisjoint(int classA, int classB)
    {
        var a = TryGetAllocatable(classA);
        var b = TryGetAllocatable(classB);
        if (a == null || b == null) return false;

        foreach (var r in a)
            if (RegisterAllocationSupport.Contains(b, r))
                return false;
        return true;
    }

    private int[]? TryGetAllocatable(int classId)
    {
        try
        {
            return _registerInfo.GetAllocatableRegisters(classId).ToArray();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
