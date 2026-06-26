using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// SplitKit S2 tests: SplitAtPoint + the CFG-reachability rename helper.
//
// SplitAtPoint cuts a value's live range at a precise program point, relocates the
// post-point portion into a fresh vreg V' with a boundary `V' = pseudo.copy value`,
// and renames exactly the downstream uses that read the relocated value — chosen by
// CFG reachability (dominance + stop-at-redef), NOT by program-order slot windows.
//
// Each shape below is one the naive slot-window approach (RelocateAcrossClobber's
// (producingDefSlot, nextDefSlot) window) gets WRONG in a branching/looping CFG:
// a use can fall inside the window yet read a different reaching definition. These
// tests pin the value-number-correct behaviour.
//
// Nothing in the allocator calls SplitAtPoint yet (that is S3); only these tests do.
public sealed class SplitEditorTests
{
    private static readonly MOS6502RegisterInfo RegInfo = new();

    static SplitEditorTests()
    {
        // Register the core/arith/cf/pseudo + mos6502 dialects.
        _ = new MOS6502Target();
    }

    private static readonly LiveIntervalsAnalysis Analysis = new();

    // The flexible class id / name V' is minted in (the relocation default).
    private static readonly int FlexId = RegInfo.FlexibleI8ClassId;

    // ---- assertion helpers --------------------------------------------------

    // The vreg id read by `instr` at the given operand index (must be a use).
    private static int UseRegId(MirInstruction instr, int index)
    {
        var op = instr.Operands[index];
        return op is VirtualReg { IsDefinition: false } v
            ? v.Id
            : throw new InvalidOperationException($"operand {index} is not a vreg use: {op}");
    }

    // Does `instr` read `vreg` at any operand?
    private static bool Reads(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
            if (op is VirtualReg { IsDefinition: false } v && v.Id == vreg) return true;
        return false;
    }

    // The single fresh vreg V' minted by a successful SplitAtPoint (recorded in the
    // split-products set). Asserts exactly one was minted.
    private static int SoleProduct(ISet<int> products)
    {
        if (products.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly one split product, got {products.Count}");
        return products.First();
    }

    // The boundary copy `V' = pseudo.copy value` inserted by the split.
    private static MirInstruction BoundaryCopy(MirBlock block, int prime, int value)
    {
        foreach (var instr in block.Instructions)
            if (instr.Opcode.Dialect == PseudoDialect.Id
                && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy
                && instr.Operands is [VirtualReg { IsDefinition: true } d, VirtualReg { IsDefinition: false } s]
                && d.Id == prime && s.Id == value)
                return instr;
        throw new InvalidOperationException("boundary copy not found");
    }

    private static SplitEditor NewEditor(MirFunction fn) => new(fn, RegInfo);

    // =========================================================================
    // 1. Straight-line after-def relocation (the baseline the old code already
    //    got right). def, [split after def], later uses → all later uses move to
    //    V'; the def's own/earlier reads stay.
    //
    //   %0 = arith.constant 1      ; i0 — def value (split AFTER here)
    //   %1 = arith.addi %0, %0     ; i1 — later use  → moves to V'
    //   %2 = arith.addi %0, %0     ; i2 — later use  → moves to V'
    // =========================================================================
    [Test]
    public async Task StraightLineAfterDef_RenamesAllLaterUses()
    {
        var fn = new MirFunction("s_straight", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        var i2 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, i0, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // The boundary copy reads the original value (never renamed).
        var copy = BoundaryCopy(bb0, prime, v0);
        await Assert.That(Reads(copy, v0)).IsTrue();

        // Both later uses now read V'.
        await Assert.That(UseRegId(i1, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(i1, 2)).IsEqualTo(prime);
        await Assert.That(UseRegId(i2, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(i2, 2)).IsEqualTo(prime);
        // The def is untouched.
        await Assert.That(((VirtualReg)i0.Operands[0]).Id).IsEqualTo(v0);
    }

    // =========================================================================
    // 2. Two-block live-out. def + use in A, split after the def in A, use in
    //    successor B → both the A-after-split use AND the B use move to V'.
    //
    //   bb0: %0 = arith.constant 1   ; i0 — def (split AFTER)
    //        %1 = arith.addi %0, %0  ; i1 — A use  → V'
    //        cf.br bb1
    //   bb1: %2 = pseudo.copy %0     ; i2 — B use  → V'
    // =========================================================================
    [Test]
    public async Task TwoBlockLiveOut_RenamesInBothBlocks()
    {
        var fn = new MirFunction("s_xblock", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb1, []));
        var i2 = bb1.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v2, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, i0, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // A-after-split use and B use both moved to V'.
        await Assert.That(UseRegId(i1, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(i2, 1)).IsEqualTo(prime);
    }

    // =========================================================================
    // 3. Sibling-block NON-reachability (the slot-window bug). A diamond: split in
    //    one arm; a use sits in the SIBLING arm whose slot number falls inside the
    //    naive (def, nextDef) window but is NOT reached from the split point (it
    //    reads a different reaching def of the value). It must NOT be renamed.
    //
    //   bb0: %c = arith.constant 0
    //        cf.cond_br %c, bb1, bb2
    //   bb1: %0 = arith.constant 1   ; i_armA — def on arm A (split AFTER)
    //        %1 = arith.addi %0,%0   ; i_useA — arm-A use  → V'
    //        cf.br bb3
    //   bb2: %0 = arith.constant 2   ; i_armB — a DIFFERENT def of %0
    //        %2 = arith.addi %0,%0   ; i_useB — arm-B use  → MUST STAY %0
    //        cf.br bb3
    //   bb3: %3 = pseudo.copy %0     ; i_join — join use (reads merged value, not
    //                                  the arm-A split value) → MUST STAY %0
    //
    //   The slot window from i_armA's def to %0's next def (i_armB) spans i_useA
    //   AND i_useB, so a slot-window split would wrongly rename i_useB. CFG
    //   reachability/dominance excludes it (bb2 is not dominated by the split in
    //   bb1; the join bb3 is reached from the sibling arm too).
    // =========================================================================
    [Test]
    public async Task SiblingBlockNotReachable_NotRenamed()
    {
        var fn = new MirFunction("s_diamond", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var bb2 = fn.CreateBlock();
        var bb3 = fn.CreateBlock();
        var c = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v3 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(c, true), new Immediate(0));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.CondBr),
            new VirtualReg(c, false), new BlockTarget(bb1, []), new BlockTarget(bb2, []));

        var armA = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var useA = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb1.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb3, []));

        bb2.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(2));
        var useB = bb2.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb2.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb3, []));

        var join = bb3.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v3, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, armA, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // Arm-A use moved to V'.
        await Assert.That(UseRegId(useA, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(useA, 2)).IsEqualTo(prime);
        // Sibling-arm use must NOT be renamed — it reads bb2's own def of %0.
        await Assert.That(UseRegId(useB, 1)).IsEqualTo(v0);
        await Assert.That(UseRegId(useB, 2)).IsEqualTo(v0);
        // The join use must NOT be renamed — it reads the merged value, reachable
        // from the sibling arm too (not dominated by the arm-A split).
        await Assert.That(UseRegId(join, 1)).IsEqualTo(v0);
    }

    // =========================================================================
    // 4. Redef on one path. A diamond where the split is BEFORE the branch (in the
    //    common predecessor): path X re-defines `value` then uses it after the
    //    redef (must NOT rename — reads the fresh value); path Y uses it with no
    //    redef (must rename to V').
    //
    //   bb0: %0 = arith.constant 1   ; i0 — def value
    //        cf.cond_br %0, bb1, bb2  ; split AFTER this terminator's def-of-nothing?
    //
    //   To get a clean "split then two paths" we split AFTER i0 (the def). The
    //   split point dominates both arms.
    //
    //   bb1 (path X): %0 = arith.constant 9  ; redefX — re-defines value
    //                 %1 = arith.addi %0,%0  ; useX — reads the FRESH value → STAY
    //   bb2 (path Y): %2 = arith.addi %0,%0  ; useY — reads relocated value → V'
    // =========================================================================
    [Test]
    public async Task RedefOnOnePath_RenamesOnlyTheRedeffreePath()
    {
        var fn = new MirFunction("s_redef", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var bb2 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.CondBr),
            new VirtualReg(v0, false), new BlockTarget(bb1, []), new BlockTarget(bb2, []));

        // Path X: re-defines %0, then uses the fresh value.
        bb1.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(9));
        var useX = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        // Path Y: uses the relocated value with no redef.
        var useY = bb2.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, i0, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // The cond_br's own read of %0 is after the split (split is after i0) and
        // dominates everything — but it reads the relocated value: it moves to V'.
        // Path X's use reads the FRESH redef value → must stay %0.
        await Assert.That(UseRegId(useX, 1)).IsEqualTo(v0);
        await Assert.That(UseRegId(useX, 2)).IsEqualTo(v0);
        // Path Y's use reads the relocated value → V'.
        await Assert.That(UseRegId(useY, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(useY, 2)).IsEqualTo(prime);
    }

    // =========================================================================
    // 5. Loop back-edge. A value defined before a loop, split after the def, used
    //    in the loop body. The body use is dominated by the split → renamed. A use
    //    in the loop header BEFORE the body (or the value flowing round the back-
    //    edge) is handled correctly: the back-edge re-entry of the split block must
    //    NOT rename the split block's pre-split region.
    //
    //   bb0 (preheader): %0 = arith.constant 1   ; i0 — def (split AFTER)
    //                    cf.br bb1
    //   bb1 (loop body): %1 = arith.addi %0,%0   ; useBody — dominated by split → V'
    //                    cf.cond_br %1, bb1, bb2  ; back-edge to bb1, exit to bb2
    //   bb2 (exit):      %2 = pseudo.copy %0      ; useExit — dominated by split → V'
    //
    //   The split point (in bb0) dominates bb1 and bb2 (every path to them crosses
    //   bb0's split region), so both uses relocate. bb1's back-edge to itself does
    //   not cause the split block (bb0) to be re-entered.
    // =========================================================================
    [Test]
    public async Task LoopBackEdge_RenamesDominatedBodyAndExitUses()
    {
        var fn = new MirFunction("s_loop", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var bb2 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v2 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb1, []));

        var useBody = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb1.AddInstruction(CfDialect.OpRef(CfOp.CondBr),
            new VirtualReg(v1, false), new BlockTarget(bb1, []), new BlockTarget(bb2, []));

        var useExit = bb2.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v2, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, i0, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // Loop-body use and exit use both dominated by the split → relocated.
        await Assert.That(UseRegId(useBody, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(useBody, 2)).IsEqualTo(prime);
        await Assert.That(UseRegId(useExit, 1)).IsEqualTo(prime);
    }

    // =========================================================================
    // 6. Not-live-across-the-split → no edit. If the value dies AT the split point
    //    (its only use is `at` itself and we split AFTER `at`), there is nothing to
    //    relocate: SplitAtPoint returns false and mints nothing.
    //
    //   %0 = arith.constant 1     ; i0 — def value
    //   %1 = pseudo.copy %0       ; i1 — sole use of value (split AFTER → dead)
    // =========================================================================
    [Test]
    public async Task NotLiveAcrossSplit_ReturnsFalse()
    {
        var fn = new MirFunction("s_dead", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        // %0 dies at i1's use point; splitting AFTER i1 has no value live → false.
        var ok = NewEditor(fn).SplitAtPoint(v0, i1, SplitEditor.InsertSide.After, li, products);

        await Assert.That(ok).IsFalse();
        await Assert.That(products.Count).IsEqualTo(0);
    }

    // =========================================================================
    // 7. Before-side split. Splitting BEFORE an instruction relocates `at`'s OWN
    //    read of the value (and all later reachable reads). The copy lands before
    //    `at`.
    //
    //   %0 = arith.constant 1      ; i0 — def
    //   %1 = arith.addi %0, %0     ; i1 — split BEFORE here → its reads move to V'
    // =========================================================================
    [Test]
    public async Task BeforeSide_RenamesAtsOwnRead()
    {
        var fn = new MirFunction("s_before", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(v0, i1, SplitEditor.InsertSide.Before, li, products);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);

        // i1's own reads move to V'; the boundary copy sits immediately before i1.
        await Assert.That(UseRegId(i1, 1)).IsEqualTo(prime);
        await Assert.That(UseRegId(i1, 2)).IsEqualTo(prime);
        var copyIdx = bb0.Instructions.IndexOf(BoundaryCopy(bb0, prime, v0));
        var i1Idx = bb0.Instructions.IndexOf(i1);
        await Assert.That(copyIdx).IsEqualTo(i1Idx - 1);
    }

    // =========================================================================
    // 8. Override class. When given an explicit class id, V' is minted in it rather
    //    than the flexible default.
    // =========================================================================
    [Test]
    public async Task OverrideClass_MintsInGivenClass()
    {
        var fn = new MirFunction("s_override", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);
        var products = new HashSet<int>();
        var ok = NewEditor(fn).SplitAtPoint(
            v0, i0, SplitEditor.InsertSide.After, li, products,
            overrideClassId: MOS6502RegisterClass.Xc);

        await Assert.That(ok).IsTrue();
        var prime = SoleProduct(products);
        var ann = fn.GetVRegAnnotation(prime) as ClassedVReg;
        await Assert.That(ann).IsNotNull();
        await Assert.That(ann!.ClassId).IsEqualTo(MOS6502RegisterClass.Xc);
    }
}
