using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// RegisterAllocatorPass — graph-colouring register allocation with iterated
// register coalescing (Chaitin / Briggs / George–Appel).
// =============================================================================
//
// Reference: Appel, *Modern Compiler Implementation* ch. 11. The worklist
// structure, the simplify/coalesce/freeze/select loop, Briggs' conservative
// coalescing test and George's precoloured test are all taken directly from
// that chapter. A reader who has not written a register allocator should be
// able to follow the algorithm from the comments here together with the
// "Reader's primer" in notes/register-allocator-redesign-plan.md.
//
// Input:  post-isel, post-PhiElim, post-TwoAddress MIR. Every vreg has a
//         ClassedVReg annotation; no block parameters; tied uses already
//         materialised as a pseudo.copy that re-defines the def vreg.
// Output: physreg-only MIR. Every VirtualReg operand has been replaced with a
//         PhysicalReg. Block live-ins are recomputed against the final
//         assignment. The vreg annotation table is cleared.
//
// -----------------------------------------------------------------------------
// What this phase changed (Phase 3 of the register-allocator redesign)
// -----------------------------------------------------------------------------
// The previous pass was linear scan wrapped in compensating machinery: three
// "widen to flexible class" pre-passes, InsertConstraintFixupCopies,
// InsertResultPreservationCopies, a single-direction copy hint with a
// starvation guard, and a TryGetDeadCopySourcePhysReg special case. ALL of
// that is deleted here. Iterated register coalescing subsumes every one of
// them:
//
//   * The widen passes existed because instruction selection pins flexible
//     values to single-physreg classes (e.g. `ac`). We no longer widen the
//     *class*; instead each vreg's allocatable register set is the
//     INTERSECTION of the register classes implied by all its defs and uses
//     (AllowedRegistersOf below). A value that is only ever `ac` keeps $a; a
//     value that is `ac` at one use but `any8` elsewhere is free to colour
//     anywhere $a/$x/zp. This is plan §3.2's sanctioned "interim path":
//     compute the class-intersection in RA rather than relocating constraint
//     copies into isel.
//
//   * InsertConstraintFixupCopies / InsertResultPreservationCopies inserted
//     copies to relieve class pressure. The coalescer makes them unnecessary:
//     a tied-def whose result is read later simply does not coalesce with the
//     next chain link if doing so would make the graph uncolourable, and a
//     real move survives.
//
//   * The copy-hint hack and TryGetDeadCopySourcePhysReg are the trivial cases
//     of coalescing — a copy whose two ends can share a register vanishes; a
//     dead copy is a copy that coalesces with its source and disappears.
//
// Spilling IS implemented (Phase 4). We use Briggs' OPTIMISTIC colouring: a node
// that cannot be proven low-degree is pushed onto the select stack anyway ("spill
// candidate") in the hope a colour is free when we pop it. A genuine select
// failure — a popped node with no free colour — becomes an actual spill, which
// SpillVregs (below) rewrites to memory traffic before the colouring is re-run.
public sealed class RegisterAllocatorPass(TargetRegisterInfo registerInfo, bool useGreedy = false) : MirFunctionPass
{
    public override string Name => "RegisterAllocator";

    public override void Run(MirFunction function)
    {
        // Relocation-copy insertion: a value produced in a single-physreg-class
        // register (e.g. a `mos6502.adc` result, hard-constrained to $a by its
        // def operand class) that is read again AFTER another instruction
        // re-defines that same physreg cannot stay there — there is one $a and
        // two values needing it live at once. We insert a `pseudo.copy` into a
        // FLEXIBLE-class fresh vreg right after such a def and redirect the later
        // uses to it. This does NOT decide a register — it merely materialises
        // the move the coalescer then relocates onto whatever register is free
        // (the reference parks it in $y; we park it in the zp pool). It is the
        // standard "result must vacate a constrained reg" shape; the coalescer
        // consumes it exactly like the tied-operand and phi copies (plan §3.3).
        //
        // (This is the surviving, genuinely-load-bearing remnant of the old
        // InsertResultPreservationCopies: without an explicit move in the IR no
        // colourer — coalescing or otherwise — can relocate a value off a
        // single-physreg class, because there is no instruction to carry it.
        // The old heuristic register choice and its multi-block TODO are gone;
        // the coalescer now picks the register from real interference.)
        var flexibleI8 = registerInfo.FlexibleI8ClassId;
        if (flexibleI8 != 0)
            InsertRelocationCopiesForConstrainedDefs(function, flexibleI8);

        // ---------------------------------------------------------------------
        // The spilling loop (Appel §11.4 "main"): build intervals → colour. If
        // colouring produces actual spills, rewrite each spilled vreg to memory
        // traffic (rematerialize cheap defs, else store-after-def / reload-
        // before-use) and go round again with fresh intervals. The loop
        // terminates because every rewrite either rematerializes the value
        // (removing the long live range entirely) or replaces it with tiny
        // reload/spill temporaries that are marked UNSPILLABLE — so the set of
        // spillable long ranges strictly shrinks each round.
        // ---------------------------------------------------------------------
        var unspillable = new HashSet<int>();
        // Reconciliation temps minted by the split-to-register spill strategy
        // (TrySplitToRegister). Tracked so they are never themselves re-split —
        // a temp's only use is the constraining one, so splitting it again would
        // just mint another copy in front of the same use, forever.
        var reconciliationTemps = new HashSet<int>();
        // Greedy-engine per-vreg stages, persisted across rounds (a deferred or
        // split range keeps its stage). Unused by the colouring engine.
        var greedyStages = new Dictionary<int, LiveRangeStage>();
        // Greedy-engine instruction-split products, persisted across rounds so a
        // reconciliation temp is never itself re-split. Unused by the colourer.
        var greedySplitProducts = new HashSet<int>();
        // Greedy-engine well-founded termination measure from the PREVIOUS split
        // round (total constrained-class live-range length). Each split round must
        // strictly reduce it; long.MaxValue = no split round seen yet. Unused by
        // the colourer.
        var previousSplitMeasure = long.MaxValue;
        LiveIntervals intervals;
        Dictionary<int, int>? assignment;
        IReadOnlyList<int> spilledVregs;

        // Safety bound: the number of spill rounds is bounded by the number of
        // distinct vregs (each round removes at least one from the spillable
        // pool). The cap turns a hypothetical non-termination bug into a clear
        // failure rather than a hang.
        var maxRounds = function.VirtualRegisterIds.Count + 1;
        var round = 0;
        while (true)
        {
            if (round++ > maxRounds)
                throw new InvalidOperationException(
                    $"RegisterAllocator: spilling did not converge for @{function.Name} " +
                    $"after {maxRounds} rounds — likely an unspillable interference cycle.");

            // Physreg-aware live intervals (with holes) are the single source of
            // interference truth. Recomputed each round because spill rewriting
            // mutates the IR.
            intervals = new LiveIntervalsAnalysis().Compute(function);

            if (useGreedy)
            {
                var greedy = new GreedyRegisterAllocator(
                    function, registerInfo, intervals, greedyStages, greedySplitProducts,
                    unspillable, round);
                assignment = greedy.Run();
                spilledVregs = greedy.SpilledVregs;

                // Terminating stage ladder (Stage R2): a RELOCATION split round —
                // the constrained-def-result / incumbent / relocatable-phys-clobber
                // kinds, the only ones that could loop (the Mode-B failure: a donor
                // whose across-clobber liveness never shrinks) — MUST strictly
                // reduce the well-founded measure: the count of constrained vregs
                // still spanning a clobber. Each relocation clears exactly one such
                // span, so the count strictly decreases and is bounded below by
                // zero. If it ever fails to, the relocation is not making progress:
                // fail loudly here, at the cause, rather than spinning to the round
                // cap. (The other split kinds terminate by their own construction —
                // split products are never re-split — so they are not policed here;
                // their measure can legitimately differ round to round.)
                if (assignment == null && greedy.DidSplit && greedy.LastSplitWasRelocation)
                {
                    if (greedy.ConstrainedRangeMeasure >= previousSplitMeasure)
                        throw new InvalidOperationException(
                            $"RegisterAllocator: greedy relocation split for @{function.Name} did " +
                            $"not reduce the constrained-clobber-span count (was " +
                            $"{previousSplitMeasure}, now {greedy.ConstrainedRangeMeasure}) — " +
                            $"non-terminating split.");
                    previousSplitMeasure = greedy.ConstrainedRangeMeasure;
                }
            }
            else
            {
                var allocator = new GraphColouringAllocator(
                    function, registerInfo, intervals, unspillable);
                assignment = allocator.Run();
                spilledVregs = allocator.SpilledVregs;
            }

            if (assignment != null)
                break; // allocated successfully — no actual spills this round.

            // Allocation edited the IR (a spill — or, under greedy, a split that
            // left SpilledVregs empty). Relieve each actual spill. Preferred order
            // (mirrors llvm-mos greedy: split-to-register before spill-to-memory):
            // rematerialize cheap defs, else split the live range into the zp
            // register file (TrySplitToRegister), else fall back to memory. An
            // empty spill list (a pure split edit) makes this a no-op and we just
            // reanalyse.
            SpillVregs(function, spilledVregs, unspillable, reconciliationTemps);
        }

        // assignment is non-null here: the loop only `break`s when colouring
        // succeeded (Run returned a non-null map).
        ApplyAssignment(function, assignment!);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, intervals, assignment!);
    }

    // =========================================================================
    // Spilling (register-allocator redesign Phase 4; plan §3.4)
    // =========================================================================
    //
    // For each vreg the colourer could not place, we lower it to memory: the
    // value lives in an abstract frame slot, with a store after its definition
    // and a reload before each use. Before falling back to that store/reload we
    // try REMATERIALIZATION — if the value is trivially recomputable (its
    // defining instruction reads no registers, only immediates/symbols), we
    // simply clone that instruction in front of each use, which is cheaper than
    // any memory round-trip and removes the long live range entirely.
    //
    // Both rewrites shatter the spilled value's single long live range into many
    // tiny ones (one per use), dropping register pressure so the next colouring
    // round succeeds. The fresh per-use vregs are recorded as UNSPILLABLE so a
    // pathological program cannot spill them again forever (Appel §11.4).
    private void SpillVregs(
        MirFunction function, IReadOnlyList<int> spills,
        HashSet<int> unspillable, HashSet<int> reconciliationTemps)
    {
        foreach (var spilled in spills)
        {
            var def = function.GetDefinition(spilled);

            if (def != null && IsRematerializable(def))
                Rematerialize(function, spilled, def, unspillable);
            else if (TrySplitToRegister(function, spilled, reconciliationTemps))
                continue;
            else
                StoreReloadSpill(function, spilled, def, unspillable);
        }
    }

    // -------------------------------------------------------------------------
    // Split-to-register spill (the llvm-mos greedy `tryInstructionSplit`
    // analogue — "effectively spilling to a register"). When a value whose
    // register class is a strict subset of the flexible class (e.g. an absolute
    // store's Axy = {$a,$x,$y}) cannot be coloured, the cause is usually that
    // several such values are simultaneously live and the narrow class has too
    // few registers — even though the abundant zero-page register file is free.
    // Rather than round-trip the value through memory, KEEP it in the flexible
    // class (so RA can park it in zero page) and copy it into the narrow class
    // only at each constraining use. The value's def must be a copy so that, once
    // its narrowing uses are reconciled away, it is touched only by copies and
    // the colourer widens it to the flexible class (GraphColouringAllocator
    // ComputeAllowedColours). This mirrors greedy inflating the class via
    // getLargestLegalSuperClass and splitting around each constraint-narrowing
    // use; it skips copies and only acts where the split actually relaxes a
    // constraint (greedy's two guards).
    //
    // Returns true if it split (the caller then re-colours); false to fall back
    // to memory (genuine pressure: even the flexible class is exhausted, or the
    // value is not eligible — a constraining def, or already a reconciliation
    // temp whose sole use is the constraint).
    private bool TrySplitToRegister(
        MirFunction function, int spilled, HashSet<int> reconciliationTemps)
    {
        var flexibleI8 = registerInfo.FlexibleI8ClassId;
        if (flexibleI8 == 0) return false;

        // A temp we already minted: its only use is the constraining one, so
        // re-splitting would loop. Send it to memory instead.
        if (reconciliationTemps.Contains(spilled)) return false;

        // The def must be non-constraining (a copy). A value defined by a
        // constraining op stays pinned to that op's class no matter how its uses
        // are rewritten, so splitting the uses cannot free it.
        var def = function.GetDefinition(spilled);
        if (def == null || !IsCopyInstr(def)) return false;

        var flexRegs = registerInfo.GetAllocatableRegisters(flexibleI8).ToArray();

        // Narrowing non-copy uses: an operand whose class is a strict subset of
        // the flexible class. (Copies impose no class and are skipped.)
        var narrowingUses = new List<(MirInstruction Instr, int Index)>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (IsCopyInstr(instr)) continue;
                var classes = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).OperandClasses;
                if (classes == null) continue;

                var operands = instr.Operands;
                for (var k = 0; k < operands.Length && k < classes.Length; k++)
                {
                    if (operands[k] is not VirtualReg u || u.IsDefinition || u.Id != spilled)
                        continue;
                    var opClass = classes[k];
                    if (opClass == 0 || opClass == flexibleI8) continue;
                    var opRegs = registerInfo.GetAllocatableRegisters(opClass);
                    if (IsStrictSubset(opRegs, flexRegs))
                        narrowingUses.Add((instr, k));
                }
            }

        if (narrowingUses.Count == 0) return false; // nothing to relax.

        var flexName = registerInfo.GetRegisterClassName(flexibleI8) ?? $"class{flexibleI8}";
        var builder = new MirBuilder(function);
        foreach (var (instr, index) in narrowingUses)
        {
            var temp = function.CreateVirtualRegisterInClass(flexibleI8, flexName);
            reconciliationTemps.Add(temp);
            builder.SetInsertionPointBefore(instr);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(temp, IsDefinition: true),
                new VirtualReg(spilled, IsDefinition: false));
            instr.Operands[index] = new VirtualReg(temp, IsDefinition: false);
        }
        return true;
    }

    private static bool IsCopyInstr(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;

    // True iff every register in `sub` is in `super` and `super` has more — i.e.
    // `sub` ⊊ `super`, so constraining a flexible value to `sub` genuinely loses
    // registers (and splitting around the use buys the value the wider class).
    private static bool IsStrictSubset(ReadOnlySpan<int> sub, int[] super)
    {
        if (sub.Length >= super.Length) return false;
        foreach (var r in sub)
            if (!Contains(super, r)) return false;
        return true;
    }

    private static bool Contains(int[] set, int value)
    {
        foreach (var s in set)
            if (s == value) return true;
        return false;
    }

    // -------------------------------------------------------------------------
    // Rematerialization: the value's def reads no registers (constant / symbol
    // address byte), so it can be recomputed anywhere. Clone the def instruction
    // immediately before each use, into a fresh vreg of the spilled vreg's class,
    // and redirect that use. Finally delete the original def (now dead).
    //
    // Rematerializable instructions on this target after isel are exactly the
    // ones whose only operands are the def plus Immediate / Symbol operands —
    // e.g. a selected i8 constant `%v = pseudo.copy <imm>`, or a symbol-byte
    // load `%v = mos6502.lda.imm.symlo @g`. See IsRematerializable.
    // -------------------------------------------------------------------------
    private static void Rematerialize(
        MirFunction function, int spilled, MirInstruction def, HashSet<int> unspillable)
    {
        var (classId, className) = ClassOf(function, spilled);
        var builder = new MirBuilder(function);

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (!UsesVreg(instr, spilled)) continue;

                // A fresh copy of the def, producing a fresh vreg, placed right
                // before this use. Each use gets its own recomputation.
                var fresh = function.CreateVirtualRegisterInClass(classId, className);
                unspillable.Add(fresh);

                builder.SetInsertionPointBefore(instr);
                builder.BuildInstruction(def.Opcode, CloneDefOperands(def, fresh));

                // The reload/remat we just inserted shifted indices by one.
                i++;
                ReplaceUseInInstruction(instr, spilled, fresh);
            }
        }

        // The original def is now dead (all uses recompute their own value).
        def.Parent?.Instructions.Remove(def);
    }

    // -------------------------------------------------------------------------
    // Store/reload spill: park the value in an abstract frame slot. Store the
    // def's result into the slot right after the def; reload it into a fresh
    // tiny vreg before each use.
    //
    // ---- The abstract spill-slot SHAPE (the contract; plan §3.4 & §8) --------
    // A spill slot is a `MirFunction.FrameSlot(Index, Type, SymbolName)` — the
    // SAME representation FrameLoweringPass and the future static-stack pass
    // already consume (notes/mos6502-codegen-quality-plan.md: "MirFunction.FrameSlots
    // … FrameLoweringPass materialises each slot as a zero-init MirGlobal and
    // rewrites mem.frame_addr <i> → mem.symbol @<slot_name>"). RA mints the slot
    // (index + i8 type + unique symbol name) but assigns it NO physical address.
    // The store/reload are emitted as the post-RA pseudo ops `pseudo.spill
    // <slot>, $reg` and `$reg = pseudo.reload <slot>`, which carry only the
    // abstract slot INDEX. A future MOS6502 static-stack pass lowers each to a
    // concrete `sta.zp`/`lda.zp` (zero-page frame, RC20..RC29 budget) or a `.bss`
    // store/load once it has coloured the call graph — exactly Layer 3 of the
    // static-stack plan ("Rewrite slot accesses to lda.zp/sta.zp at the assigned
    // address"). This decoupling — RA mints abstract slots, static-stack places
    // them — is what lets RA run before static-stack (plan §8).
    //
    // We emit dedicated `pseudo.spill`/`pseudo.reload` ops rather than generic
    // `mem.store`/`mem.load` because RA runs AFTER legalization and instruction
    // selection: a generic memory op minted here would never be legalized or
    // selected, whereas a dedicated post-RA spill op is the thing the future
    // static-stack pass pattern-matches directly. The frame-slot type is i8
    // because every value reaching RA is a post-legalization byte.
    // -------------------------------------------------------------------------
    private static void StoreReloadSpill(
        MirFunction function, int spilled, MirInstruction? def, HashSet<int> unspillable)
    {
        var (classId, className) = ClassOf(function, spilled);
        var slot = PickSpillSlot(function, spilled);
        var builder = new MirBuilder(function);

        // Store after the def (if there is one — a livein/param vreg may have no
        // in-function def, in which case its value is already where the reloads
        // expect, and only reloads are needed... but such a value cannot be
        // spilled meaningfully, so we require a def here).
        if (def != null)
        {
            var defBlock = def.Parent!;
            var defIdx = defBlock.Instructions.IndexOf(def);
            defBlock.InsertInstruction(
                defIdx + 1,
                PseudoDialect.OpRef(PseudoOp.Spill),
                new Immediate(slot.Index),
                new VirtualReg(spilled, IsDefinition: false));
        }

        // Reload before each use into a fresh tiny vreg, and redirect that use.
        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                // Skip the store we may have just inserted (it USES `spilled` by
                // design and must keep doing so).
                if (IsSpillStoreOf(instr, slot.Index)) continue;
                if (!UsesVreg(instr, spilled)) continue;

                var fresh = function.CreateVirtualRegisterInClass(classId, className);
                unspillable.Add(fresh);

                builder.SetInsertionPointBefore(instr);
                builder.BuildInstruction(
                    PseudoDialect.OpRef(PseudoOp.Reload),
                    new VirtualReg(fresh, IsDefinition: true),
                    new Immediate(slot.Index));

                i++; // account for the inserted reload.
                ReplaceUseInInstruction(instr, spilled, fresh);
            }
        }
    }

    // A vreg is rematerializable iff its def is side-effect-free and reads no
    // registers — every non-def operand is an Immediate or Symbol, so the value
    // can be recomputed at any program point with no dependencies. This catches
    // selected constants (`pseudo.copy <imm>`) and symbol-address bytes
    // (`mos6502.lda.imm.sym* @g`). (Appel §11.4 "rematerialization".)
    private static bool IsRematerializable(MirInstruction def)
    {
        var dialect = DialectRegistry.ById(def.Opcode.Dialect);
        if (!dialect.IsSideEffectFree(def.Opcode.Code)) return false;

        var sawDef = false;
        foreach (var op in def.Operands)
        {
            switch (op)
            {
                case VirtualReg v when v.IsDefinition:
                    sawDef = true;
                    break;
                case PhysicalReg { IsDefinition: true }:
                    sawDef = true;
                    break;
                case VirtualReg:   // a register USE — not rematerializable.
                case PhysicalReg:
                    return false;
                case Immediate:
                case Symbol:
                    break;        // safe: always available.
                default:
                    return false; // BlockTarget etc. — be conservative.
            }
        }
        return sawDef;
    }

    // Clone a rematerializable def's operands, swapping the def vreg for `fresh`.
    private static MirOperand[] CloneDefOperands(MirInstruction def, int fresh)
    {
        var clone = new MirOperand[def.Operands.Length];
        for (var i = 0; i < def.Operands.Length; i++)
            clone[i] = def.Operands[i] is VirtualReg { IsDefinition: true }
                ? new VirtualReg(fresh, IsDefinition: true)
                : def.Operands[i];
        return clone;
    }

    // The class of an existing vreg (so reload/remat temps share its class).
    private static (int ClassId, string Name) ClassOf(MirFunction function, int vreg) =>
        function.GetVRegAnnotation(vreg) is ClassedVReg c
            ? (c.ClassId, c.Name)
            : throw new InvalidOperationException(
                $"RegisterAllocator: cannot spill vreg %{vreg} — it has no register class.");

    // Mint a fresh abstract frame slot for a spilled vreg. The slot is i8 (every
    // RA-time value is a byte), uniquely named per function+vreg so distinct
    // spills never alias, and registered on the function so the future
    // static-stack pass can enumerate and place it. RA assigns NO address.
    private static FrameSlot PickSpillSlot(MirFunction function, int vreg)
    {
        var index = function.FrameSlots.Count;
        var slot = new FrameSlot(index, IRType.I8, $"{function.Name}_spill{vreg}");
        function.FrameSlots.Add(slot);
        return slot;
    }

    private static bool IsSpillStoreOf(MirInstruction instr, int slotIndex) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Spill
        && instr.Operands.Length >= 1
        && instr.Operands[0] is Immediate imm
        && imm.Value == slotIndex;

    private static bool UsesVreg(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (OperandUsesVreg(op, vreg))
                return true;
        return false;
    }

    private static bool OperandUsesVreg(MirOperand op, int vreg)
    {
        switch (op)
        {
            case VirtualReg v when !v.IsDefinition && v.Id == vreg:
                return true;
            case BlockTarget bt:
                foreach (var arg in bt.Args)
                    if (OperandUsesVreg(arg, vreg))
                        return true;
                return false;
            default:
                return false;
        }
    }

    // Redirect uses of `oldVreg` to `newVreg` within a single instruction only
    // (each use site gets its own reload/remat temp, so we cannot use the
    // function-wide ReplaceAllUsesOfRegister).
    private static void ReplaceUseInInstruction(MirInstruction instr, int oldVreg, int newVreg)
    {
        var operands = instr.Operands;
        for (var i = 0; i < operands.Length; i++)
            operands[i] = ReplaceUseInOperand(operands[i], oldVreg, newVreg);
    }

    private static MirOperand ReplaceUseInOperand(MirOperand op, int oldVreg, int newVreg) => op switch
    {
        VirtualReg v when !v.IsDefinition && v.Id == oldVreg => v with { Id = newVreg },
        BlockTarget bt => bt with { Args = ReplaceUsesInArgs(bt.Args, oldVreg, newVreg) },
        _ => op,
    };

    private static MirOperand[] ReplaceUsesInArgs(MirOperand[] args, int oldVreg, int newVreg)
    {
        var rewritten = new MirOperand[args.Length];
        for (var i = 0; i < args.Length; i++)
            rewritten[i] = ReplaceUseInOperand(args[i], oldVreg, newVreg);
        return rewritten;
    }

    // -------------------------------------------------------------------------
    // Insert a relocation pseudo.copy after each tied-operand def whose result
    // outlives the defining instruction. The fresh vreg is in the flexible 8-bit
    // class, so the coalescer is free to place the relocated value on ANY free
    // register (it does not commit to one here). Every later use of the original
    // def vreg — in this block AND in successor blocks — is redirected to the
    // fresh vreg.
    //
    // Trigger: a tied def (the def end of a two-address instruction such as
    // mos6502.adc). After TwoAddressInstructionPass the def vreg is also the
    // tied use, so it is hard-pinned to the op's single-physreg class ($a). If
    // its value outlives the instruction it MUST be moved off that register
    // before the next link in the chain re-defines it; that is what this copy
    // provides.
    //
    // Cross-block: a value live OUT of the defining block (the case the deleted
    // isel out-funnels covered by renaming ALL uses up front) is redirected
    // slot-precisely, mirroring SplitEditor.RelocateAcrossClobber and LLVM's
    // SplitKit/SplitEditor (which work off SlotIndex numbering, NOT a CFG walk):
    // a use of the constrained value is any use whose use-slot lies strictly
    // after the producing def's def-slot and strictly before the value's NEXT
    // def (so a later, unrelated re-definition's value is untouched). Because
    // LiveIntervals numbers slots in program order across every block, this
    // redirects the cross-block uses for free. The colourer's global coalescer
    // then places the single resulting cross-block vreg coherently in one
    // register.
    // -------------------------------------------------------------------------
    private void InsertRelocationCopiesForConstrainedDefs(MirFunction function, int flexibleI8)
    {
        var flexibleName = registerInfo.GetRegisterClassName(flexibleI8) ?? $"class{flexibleI8}";

        // Slot numbering is built once over the post-TwoAddress MIR. We collect
        // every relocation against this single snapshot FIRST, then apply them —
        // the recorded (instruction, operand-index) pairs survive the insertions
        // because they hold instruction references, not positions.
        var intervals = new LiveIntervalsAnalysis().Compute(function);

        var plans = new List<(MirInstruction DefInstr, int DefVreg,
            List<(MirInstruction Instr, int Index)> Uses)>();

        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var info = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code);
                var tiedOperands = info.TiedOperands;
                if (tiedOperands == null) continue;

                var operands = instr.Operands;
                for (var defIdx = 0; defIdx < operands.Length; defIdx++)
                {
                    if (operands[defIdx] is not VirtualReg defOp || !defOp.IsDefinition)
                        continue;
                    if (!IsTiedDef(tiedOperands, defIdx)) continue;

                    var defVreg = defOp.Id;
                    var producingDefSlot = LiveIntervals.DefSlot(intervals.BaseSlotOf[instr]);

                    // Earliest def of defVreg strictly after the producing one —
                    // a use at/after it reads a fresh value and must stay put.
                    var nextDefSlot = long.MaxValue;
                    foreach (var b in function.Blocks)
                        foreach (var other in b.Instructions)
                        {
                            if (!DefinesVreg(other, defVreg)) continue;
                            var s = LiveIntervals.DefSlot(intervals.BaseSlotOf[other]);
                            if (s > producingDefSlot && s < nextDefSlot) nextDefSlot = s;
                        }

                    // Collect every use of defVreg in (producingDefSlot, nextDefSlot)
                    // across ALL blocks. The defining instruction's own tied use has
                    // use-slot == base < producingDefSlot (= base + 1), so it is
                    // excluded automatically.
                    var uses = new List<(MirInstruction, int)>();
                    foreach (var b in function.Blocks)
                        foreach (var other in b.Instructions)
                        {
                            var useSlot = LiveIntervals.UseSlot(intervals.BaseSlotOf[other]);
                            if (useSlot <= producingDefSlot || useSlot >= nextDefSlot) continue;
                            var ops = other.Operands;
                            for (var k = 0; k < ops.Length; k++)
                                if (ops[k] is VirtualReg u && !u.IsDefinition && u.Id == defVreg)
                                    uses.Add((other, k));
                        }

                    // Relocate only if the value is read again; a value never read
                    // after its def needs no copy.
                    if (uses.Count > 0)
                        plans.Add((instr, defVreg, uses));
                }
            }

        foreach (var (defInstr, defVreg, uses) in plans)
        {
            var block = defInstr.Parent!;
            var tempVreg = function.CreateVirtualRegisterInClass(flexibleI8, flexibleName);
            block.InsertInstruction(
                block.Instructions.IndexOf(defInstr) + 1,
                Dialects.Pseudo.PseudoDialect.OpRef(Dialects.Pseudo.PseudoOp.Copy),
                new VirtualReg(tempVreg, IsDefinition: true),
                new VirtualReg(defVreg, IsDefinition: false));

            foreach (var (instr, index) in uses)
                instr.Operands[index] = new VirtualReg(tempVreg, IsDefinition: false);
        }
    }

    private static bool IsTiedDef(int[] tiedOperands, int defIdx)
    {
        foreach (var t in tiedOperands)
            if (t == defIdx) return true;
        return false;
    }

    private static bool DefinesVreg(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (op is VirtualReg d && d.IsDefinition && d.Id == vreg) return true;
        return false;
    }

    // -------------------------------------------------------------------------
    // Rewrite every VirtualReg operand to PhysicalReg using the final colouring.
    // -------------------------------------------------------------------------
    private static void ApplyAssignment(MirFunction function, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is not VirtualReg v) continue;
                    if (!assignment.TryGetValue(v.Id, out var physReg))
                        throw new InvalidOperationException(
                            $"RegisterAllocator: vreg %{v.Id} has no physical register assignment.");
                    operands[i] = new PhysicalReg(physReg, v.IsDefinition);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Block live-ins = physregs live on entry. The entry block's CC live-ins
    // are already populated by AbiLowering; for every other block, replace
    // whatever was there with the physregs whose vregs are live on entry, per
    // the final assignment. "Live on entry to block B" is read straight off the
    // intervals: a vreg is live-in iff its interval covers B's first use point.
    // -------------------------------------------------------------------------
    private static void RecomputeBlockLiveIns(
        MirFunction function, LiveIntervals intervals, IReadOnlyDictionary<int, int> assignment)
    {
        for (var i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            if (i == 0) continue;
            if (block.Instructions.Count == 0) { block.LiveIns.Clear(); continue; }

            var entryPoint = LiveIntervals.UseSlot(intervals.BaseSlotOf[block.Instructions[0]]);

            block.LiveIns.Clear();
            // Deterministic order (by vreg id) so the printed live-in list is
            // stable across runs — the characterization lit tests depend on it.
            foreach (var vreg in assignment.Keys.OrderBy(v => v))
                if (intervals.IntervalOf(vreg).Covers(entryPoint)
                    && assignment.TryGetValue(vreg, out var physReg)
                    && !block.LiveIns.Contains(physReg))
                    block.LiveIns.Add(physReg);
        }
    }
}
