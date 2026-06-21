using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// RegisterAllocationSupport — shared register-allocation helpers.
// =============================================================================
//
// The class-intersection ("allowed colours") computation and the
// referenced-vreg collection are needed identically by BOTH allocator engines:
// the graph-colouring `GraphColouringAllocator` and the greedy
// `GreedyRegisterAllocator`. This is the canonical copy, used by the greedy
// engine. The colourer still carries its own equivalent private copy for now —
// it is the *default* allocator and is left untouched during the greedy
// bring-up; the two converge when the colourer is retired (greedy-RA plan
// Stage 4), at which point this becomes the only copy. Until then the logic in
// the two must stay in lockstep (it is stable and not edited during bring-up).
internal static class RegisterAllocationSupport
{
    // -------------------------------------------------------------------------
    // Every vreg that actually appears in the IR (has at least one operand
    // occurrence) — the ones that need colouring. Returned ascending so callers
    // get a deterministic node order. (Orphaned vregs whose annotation lingers
    // but which are never referenced simply never appear here.)
    // -------------------------------------------------------------------------
    public static List<int> CollectReferencedVregs(MirFunction function)
    {
        var set = new SortedSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    CollectVregRefs(op, set);
        return set.ToList();
    }

    private static void CollectVregRefs(MirOperand op, SortedSet<int> into)
    {
        switch (op)
        {
            case VirtualReg v:
                into.Add(v.Id);
                break;
            case BlockTarget bt:
                foreach (var arg in bt.Args)
                    CollectVregRefs(arg, into);
                break;
        }
    }

    // =========================================================================
    // Class intersection ("allowed colours")
    // =========================================================================
    //
    // A vreg's allocatable registers are the intersection of the register
    // classes implied by all of its appearances:
    //   * its ClassedVReg annotation (the class isel/legalizer committed it to);
    //   * the operand-class constraint of every instruction operand it fills
    //     (DialectInstructionInfo.OperandClasses), when that class is non-zero.
    //
    // Intersecting these means a value that is `ac` at one operand but otherwise
    // free is constrained to $a (correct), while a value annotated `ac` only
    // because isel pinned it — but never actually required on $a by an operand
    // class — keeps the broad set its annotation's class allows. As a SIDE
    // EFFECT it re-annotates each vreg with a uniform ClassedVReg view (the same
    // ReclassifyVirtualRegister call the colourer always made).
    //
    // Returns the per-vreg preference-ordered allocatable register set.
    public static Dictionary<int, int[]> ComputeAllowedColours(
        MirFunction function, TargetRegisterInfo registerInfo, IReadOnlyList<int> vregNodes)
    {
        var allowedColours = new Dictionary<int, int[]>();

        // Operand-class constraints gathered from every use/def site, per vreg.
        // CRUCIAL: a pseudo.copy imposes NO operand-class constraint (it has no
        // OperandClasses), so an operand-class constraint is a HARD requirement.
        var operandClassConstraints = new Dictionary<int, HashSet<int>>();

        // "Copy-only" vregs — those that appear ONLY as pseudo.copy operands.
        // Such a value has no hard physreg requirement of its own: every real
        // (target-dialect) operand that would pin it to a specific physreg
        // reaches it through a copy, so the value itself is free to live
        // anywhere flexible. A value touched by a non-copy op keeps its selected
        // single-physreg class as a hard constraint (a target op like
        // `lda.imm.symhi` produces its `ac` result in $a yet carries NO declared
        // OperandClasses, so widening it would be a real miscompile).
        var touchedByNonCopy = new HashSet<int>();

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var isCopy = instr.Opcode.Dialect == PseudoDialect.Id
                    && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;
                if (!isCopy)
                    foreach (var op in instr.Operands)
                        if (op is VirtualReg v)
                            touchedByNonCopy.Add(v.Id);

                var info = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code);
                var classes = info.OperandClasses;
                if (classes == null) continue;

                var operands = instr.Operands;
                for (var i = 0; i < operands.Length && i < classes.Length; i++)
                {
                    if (classes[i] == 0) continue;
                    if (operands[i] is not VirtualReg v) continue;
                    if (!operandClassConstraints.TryGetValue(v.Id, out var set))
                        operandClassConstraints[v.Id] = set = [];
                    set.Add(classes[i]);
                }
            }
        }

        var flexibleId = registerInfo.FlexibleI8ClassId;
        var flexibleRegs = flexibleId != 0
            ? registerInfo.GetAllocatableRegisters(flexibleId).ToArray()
            : [];

        foreach (var vreg in vregNodes)
        {
            int classId;
            string className;
            switch (function.GetVRegAnnotation(vreg))
            {
                case ClassedVReg classed:
                    classId = classed.ClassId;
                    className = classed.Name;
                    break;

                // A vreg still carrying a TypedVReg annotation at RA is an 8-bit
                // value the selector never pinned to a class — it flows only
                // through pseudo.copy plumbing. Such a value is free to live
                // anywhere 8-bit, so it takes the target's flexible class. A
                // still-typed WIDER vreg would be an isel/legalizer gap.
                case TypedVReg typed when registerInfo.FlexibleI8ClassId != 0
                                          && typed.Type.SizeInBits <= 8:
                    classId = registerInfo.FlexibleI8ClassId;
                    className = registerInfo.GetRegisterClassName(classId) ?? $"class{classId}";
                    break;

                case TypedVReg typed:
                    throw new InvalidOperationException(
                        $"RegisterAllocator: vreg %{vreg} still has wide/unclassable type " +
                        $"{typed.Type.DisplayName} at RA — legalization/isel did not narrow it.");

                default:
                    throw new InvalidOperationException(
                        $"RegisterAllocator: vreg %{vreg} has no register class.");
            }

            // Re-annotate so downstream code sees a uniform ClassedVReg view.
            function.ReclassifyVirtualRegister(vreg, classId, className);

            // ---- Base set: the broadest registers this value could use -------
            // For a COPY-ONLY value whose annotation class is a single-physreg
            // subset of the flexible 8-bit class, the annotation class is only a
            // preference: the value is really free to live anywhere flexible. The
            // base set is then the flexible set, ordered with the annotation
            // register FIRST so colouring still prefers it. For a value TOUCHED BY
            // A NON-COPY op the annotation class is a HARD constraint, NOT widened.
            var annotationRegs = registerInfo.GetAllocatableRegisters(classId).ToArray();
            int[] allowed;
            if (flexibleId != 0 && classId != flexibleId
                && !touchedByNonCopy.Contains(vreg)
                && annotationRegs.All(r => Contains(flexibleRegs, r)))
            {
                // annotation regs first (preference), then the rest of flexible.
                allowed = annotationRegs
                    .Concat(flexibleRegs.Where(r => !Contains(annotationRegs, r)))
                    .ToArray();
            }
            else
            {
                allowed = annotationRegs;
            }

            // ---- Hard constraints: intersect real operand-class register sets.
            if (operandClassConstraints.TryGetValue(vreg, out var constraintClasses))
            {
                foreach (var constraintClass in constraintClasses)
                {
                    var constraintRegs = registerInfo.GetAllocatableRegisters(constraintClass).ToArray();
                    allowed = allowed.Where(r => Contains(constraintRegs, r)).ToArray();
                }
            }

            if (allowed.Length == 0)
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} in class {className} has an empty " +
                    "allocatable set after intersecting operand-class constraints.");

            allowedColours[vreg] = allowed;
        }

        return allowedColours;
    }

    public static bool Contains(ReadOnlySpan<int> values, int target)
    {
        foreach (var v in values)
            if (v == target) return true;
        return false;
    }
}
