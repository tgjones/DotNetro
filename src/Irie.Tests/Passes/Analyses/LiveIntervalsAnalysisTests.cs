using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes.Analyses;

// Phase-1 tests for the new physreg-aware LiveIntervals analysis (with holes).
// See notes/register-allocator-redesign-plan.md §3.1 / §7 "Phase 1".
//
// Sub-slot numbering reminder (LiveIntervals header): each instruction owns two
// points — UseSlot = 2*index (operands read here) and DefSlot = 2*index+1
// (results written here). Intervals are half-open [start, end).
public sealed class LiveIntervalsAnalysisTests
{
    static LiveIntervalsAnalysisTests()
    {
        // Register the core/arith/cf/pseudo dialects + the mos6502 dialect.
        _ = new MOS6502Target();
    }

    private static readonly LiveIntervalsAnalysis Analysis = new();

    // Helper: the def point of an instruction (where its results become live).
    private static int DefPt(LiveIntervals li, MirInstruction instr) =>
        LiveIntervals.DefSlot(li.BaseSlotOf[instr]);

    // Helper: the use point of an instruction (where its operands are read).
    private static int UsePt(LiveIntervals li, MirInstruction instr) =>
        LiveIntervals.UseSlot(li.BaseSlotOf[instr]);

    // -------------------------------------------------------------------------
    // 1. A vreg with a HOLE: live, dead, live again. The segments must reflect
    //    the gap — the central property the old single-[start,end] analysis
    //    could not express.
    //
    //   %0 = arith.constant 1   ; i0 — def %0
    //   %1 = arith.addi %0, %0  ; i1 — last use of %0 (its first segment ends)
    //   %2 = arith.constant 2   ; i2 — (%0 dead here: HOLE)
    //   %0 = arith.constant 3   ; i3 — %0 RE-defined (second segment opens)
    //   %4 = arith.addi %0, %0  ; i4 — use of %0 again
    // -------------------------------------------------------------------------
    [Test]
    public async Task VRegHole_TwoSegmentsWithGap()
    {
        var fn = new MirFunction("hole", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);
        var v4 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v2, true), new Immediate(2));
        var i3 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(3));
        var i4 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v4, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        var seg = li.IntervalOf(v0).Segments;
        // Exactly two segments. A value used at instruction N is live through
        // N's def point (half-open end = use point + 1 = def point), so:
        //   seg0 = [def i0, def i1)  (defined at i0, last used at i1)
        //   seg1 = [def i3, def i4)  (re-defined at i3, last used at i4)
        await Assert.That(seg.Count).IsEqualTo(2);
        await Assert.That((seg[0].Start, seg[0].End))
            .IsEqualTo((DefPt(li, i0), DefPt(li, i1)));
        await Assert.That((seg[1].Start, seg[1].End))
            .IsEqualTo((DefPt(li, i3), DefPt(li, i4)));
        // The two segments are distinct value numbers (a hole / re-def between).
        await Assert.That(seg[0].ValNo).IsNotEqualTo(seg[1].ValNo);

        // %0 is live at its first use (i1's use point) but NOT in the hole that
        // follows: the gap between the first segment's end and the re-definition
        // at i3 is uncovered.
        await Assert.That(li.IntervalOf(v0).Covers(UsePt(li, i1))).IsTrue();
        var gapPoint = DefPt(li, i1); // first instant past the first segment
        await Assert.That(li.IntervalOf(v0).Covers(gapPoint)).IsFalse();
    }

    // -------------------------------------------------------------------------
    // 2. Physreg CLOBBER intervals derived from a flag-defining op. mos6502.cmp
    //    implicit-defs $n/$z/$c (materialized as PhysicalReg(IsImplicit:true)
    //    def operands by isel). LiveIntervals must produce a busy segment for
    //    each flag physreg at the cmp — exactly what the old
    //    ComputeClobberSlots recorded.
    //
    //   bb0:
    //     %a = pseudo.copy $a            ; i0
    //     mos6502.cmp %a, %b, def $n,$z,$c (implicit)  ; i1 — clobbers flags
    // -------------------------------------------------------------------------
    [Test]
    public async Task PhysRegClobber_FromFlagDefiningOp()
    {
        var fn = new MirFunction("cmpflags", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var va = fn.CreateVirtualRegister(IRType.I8);
        var vb = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(va, true), new PhysicalReg(MOS6502Registers.A, false));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(vb, true), new Immediate(5));
        var cmp = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Cmp),
            new VirtualReg(va, false),
            new VirtualReg(vb, false),
            new PhysicalReg(MOS6502Registers.N, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Z, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.C, IsDefinition: true, IsImplicit: true));

        var li = Analysis.Compute(fn);

        // Each flag physreg has a busy segment opening at the cmp's def point.
        var cmpDef = DefPt(li, cmp);
        foreach (var flag in new[] { MOS6502Registers.N, MOS6502Registers.Z, MOS6502Registers.C })
        {
            var iv = li.PhysIntervalOf(flag);
            await Assert.That(iv.IsEmpty).IsFalse();
            await Assert.That(iv.Covers(cmpDef)).IsTrue();
        }
    }

    // -------------------------------------------------------------------------
    // 3. CC LIVE-INS produce physreg intervals from the entry block. The entry
    //    block's MirBlock.LiveIns (populated by AbiLowering) must give each
    //    argument physreg a segment that starts at block entry.
    //
    //   bb0 [liveins: $a, $x]:
    //     %0 = pseudo.copy $a   ; i0 — uses $a (the CC arg)
    //     %1 = pseudo.copy $x   ; i1 — uses $x
    // -------------------------------------------------------------------------
    [Test]
    public async Task CcLiveIns_ProducePhysRegIntervalsFromEntry()
    {
        var fn = new MirFunction("liveins", [IRType.I8, IRType.I8], IRType.Void);
        var bb0 = fn.CreateBlock();
        bb0.LiveIns.Add(MOS6502Registers.A);
        bb0.LiveIns.Add(MOS6502Registers.X);
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v0, true), new PhysicalReg(MOS6502Registers.A, false));
        var i1 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, true), new PhysicalReg(MOS6502Registers.X, false));

        var li = Analysis.Compute(fn);

        // $a is live from entry (use point of i0) through its read at i0.
        var aIv = li.PhysIntervalOf(MOS6502Registers.A);
        await Assert.That(aIv.IsEmpty).IsFalse();
        await Assert.That(aIv.Covers(UsePt(li, i0))).IsTrue();

        // $x is live from entry through its read at i1.
        var xIv = li.PhysIntervalOf(MOS6502Registers.X);
        await Assert.That(xIv.IsEmpty).IsFalse();
        await Assert.That(xIv.Covers(UsePt(li, i1))).IsTrue();
    }

    // -------------------------------------------------------------------------
    // 4. Copy-related intervals: def/use sharing across a pseudo.copy. The whole
    //    reason for sub-slots is that a value used at one instruction and a fresh
    //    value defined at the SAME instruction do NOT interfere, so they can
    //    share a register.
    //
    //   %0 = arith.constant 1   ; i0 — def %0
    //   %1 = pseudo.copy %0     ; i1 — last use of %0 (use point), def of %1
    //                                  (def point) — half-open windows disjoint.
    // -------------------------------------------------------------------------
    [Test]
    public async Task CopyDefUseSharing_DoNotInterfere()
    {
        var fn = new MirFunction("copyshare", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        // %0 is live through i1's use; under half-open numbering its segment
        // ends at i1's def point. %1 begins at i1's def point. End == Start, so
        // the two are adjacent but disjoint.
        await Assert.That(li.IntervalOf(v0).End).IsEqualTo(DefPt(li, i1));
        await Assert.That(li.IntervalOf(v1).Start).IsEqualTo(DefPt(li, i1));

        // Therefore they do NOT interfere — the copy's source and destination
        // can be coalesced onto one register (Phase 3 will exploit this).
        await Assert.That(li.Interfere(v0, v1)).IsFalse();

        // Sanity: i0's def and i1's use ARE one continuous segment for %0.
        await Assert.That(li.IntervalOf(v0).Segments.Count).IsEqualTo(1);
    }

    // A live def/use that genuinely overlaps DOES interfere (control for the
    // test above): two values both live across the same arith.addi.
    //
    //   %0 = arith.constant 1   ; i0
    //   %1 = arith.constant 2   ; i1
    //   %2 = arith.addi %0, %1  ; i2 — both %0 and %1 read here, both live
    [Test]
    public async Task TwoValuesLiveTogether_Interfere()
    {
        var fn = new MirFunction("interfere", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, true), new Immediate(2));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, true), new VirtualReg(v0, false), new VirtualReg(v1, false));

        var li = Analysis.Compute(fn);
        await Assert.That(li.Interfere(v0, v1)).IsTrue();
    }

    // -------------------------------------------------------------------------
    // 5a. REPRODUCTION (clobber): the old RA's ComputeClobberSlots marks a
    //     physreg busy at the slot of an instruction that implicit-defs it, so a
    //     vreg whose live range *crosses* that clobber cannot use the physreg.
    //     LiveIntervals must report the same: the clobbered physreg's interval
    //     OVERLAPS the crossing vreg's interval.
    //
    //   %save = pseudo.copy $a   ; i0 — %save born in $a... but really we model a
    //                                   value that must stay live across a call.
    //   jsr.abs @callee, def $a (implicit clobber)  ; i1 — clobbers $a
    //   %use  = pseudo.copy %save ; i2 — %save read AFTER the clobber
    //
    //   In the old pass: $a is a clobber slot at i1; a vreg parked in $a across
    //   i1 (def < i1 < lastUse) is rejected by IsClobberFree. Here we assert the
    //   physreg-interval analog: $a's busy interval (the jsr's implicit def)
    //   overlaps %save's interval if %save were placed on $a.
    // -------------------------------------------------------------------------
    [Test]
    public async Task Reproduction_ClobberAcrossCall_OverlapsCrossingValue()
    {
        var fn = new MirFunction("clobber", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var save = fn.CreateVirtualRegister(IRType.I8);
        var use = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(save, true), new Immediate(7));
        var jsr = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.JsrAbs),
            new Symbol("callee"),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true, IsImplicit: true));
        var i2 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(use, true), new VirtualReg(save, false));

        var li = Analysis.Compute(fn);

        // %save is live across the jsr (def i0 .. use i2). $a is busy at the jsr
        // (its implicit def). The two ranges overlap in slot order, which is the
        // exact conclusion ComputeClobberSlots reached: %save cannot live in $a.
        var saveIv = li.IntervalOf(save);
        var aIv = li.PhysIntervalOf(MOS6502Registers.A);
        await Assert.That(saveIv.Start).IsLessThanOrEqualTo(DefPt(li, jsr));
        await Assert.That(saveIv.End).IsGreaterThanOrEqualTo(DefPt(li, jsr));
        await Assert.That(aIv.Covers(DefPt(li, jsr))).IsTrue();
        // The physreg interval overlaps the vreg interval over the clobber span.
        await Assert.That(saveIv.Overlaps(aIv)).IsTrue();

        // And %use (born at i2's def point) does NOT overlap the $a clobber at
        // the jsr — so a later value MAY reuse $a, matching the old pass's
        // half-open clobber test (clobber at K, value born at K+something).
        await Assert.That(li.IntervalOf(use).Overlaps(aIv)).IsFalse();
    }

    // -------------------------------------------------------------------------
    // 5b. REPRODUCTION (reservation): the old RA's ComputePhysRegReservations
    //     tracked a call-result physreg held from its def until a
    //     `pseudo.copy %v = $P` reads it back, reserving [defSlot, readSlot] so
    //     no unrelated vreg lands on $P in that window. LiveIntervals models the
    //     identical span as the physreg's own interval: $a is live from the
    //     jsr's implicit def to the relocation copy's use point.
    //
    //   jsr.abs @callee, def $a (implicit)   ; i0 — call result in $a
    //   %r = pseudo.copy $a                   ; i1 — relocate result out of $a
    //   ... %other lives here ...
    //
    //   The reservation window the old pass computed = [i0.def, i1.use]; the new
    //   $a interval must cover exactly that, so any vreg overlapping it is
    //   excluded from $a — the same conclusion, with no bespoke reservation map.
    // -------------------------------------------------------------------------
    [Test]
    public async Task Reproduction_CallResultReservation_PhysRegIntervalCoversWindow()
    {
        var fn = new MirFunction("reservation", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var r = fn.CreateVirtualRegister(IRType.I8);
        var other = fn.CreateVirtualRegister(IRType.I8);

        var jsr = bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.JsrAbs),
            new Symbol("callee"),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true, IsImplicit: true));
        var reloc = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(r, true), new PhysicalReg(MOS6502Registers.A, false));
        // An unrelated value defined before the call would overlap the window.
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(other, true), new Immediate(9));

        var li = Analysis.Compute(fn);

        // $a's interval covers the whole reservation window [jsr.def, reloc.use]
        // — precisely the (defSlot, readSlot) span ComputePhysRegReservations
        // recorded for the consumer copy.
        var aIv = li.PhysIntervalOf(MOS6502Registers.A);
        await Assert.That(aIv.Covers(DefPt(li, jsr))).IsTrue();
        await Assert.That(aIv.Covers(UsePt(li, reloc))).IsTrue();
        // The span runs continuously from def to the relocation read.
        await Assert.That(aIv.Start).IsEqualTo(DefPt(li, jsr));
        await Assert.That(aIv.End).IsGreaterThanOrEqualTo(UsePt(li, reloc));
    }

    // -------------------------------------------------------------------------
    // 6. Cross-block hole / continuity: a vreg defined in bb0 and used only in
    //    bb1 must have a single continuous interval reaching across the branch
    //    (no spurious hole at the block boundary), while a vreg dead before the
    //    branch must NOT reach bb1.
    //
    //   bb0:
    //     %0 = arith.constant 1   ; i0 — used in bb1 (live-out)
    //     %1 = arith.constant 2   ; i1 — dead after bb0
    //     cf.br bb1               ; i2
    //   bb1:
    //     %2 = pseudo.copy %0     ; i3 — use of %0
    // -------------------------------------------------------------------------
    [Test]
    public async Task CrossBlock_LiveOutReachesSuccessor_DeadValueDoesNot()
    {
        var fn = new MirFunction("crossblock", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, true), new Immediate(2));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br),
            new BlockTarget(bb1, []));
        var i3 = bb1.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v2, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        // %0 spans from its def in bb0 to its use in bb1 — one CONTINUOUS
        // interval (no boundary hole): def point of i0 .. def point of i3
        // (its last user). The bb0 live-out segment and the bb1 live-in segment
        // abut at the block boundary and merge in Normalize.
        var iv0 = li.IntervalOf(v0);
        await Assert.That(iv0.Segments.Count).IsEqualTo(1);
        await Assert.That(iv0.Start).IsEqualTo(DefPt(li, i0));
        await Assert.That(iv0.End).IsEqualTo(DefPt(li, i3));
        await Assert.That(iv0.Covers(UsePt(li, i3))).IsTrue();

        // %1 dies in bb0 and must not reach bb1 (its end is below bb1's slots).
        await Assert.That(li.IntervalOf(v1).End).IsLessThan(DefPt(li, i3));
        await Assert.That(li.Interfere(v0, v1)).IsTrue(); // both live in bb0
    }

    // =========================================================================
    // Value-number tests (SplitKit S1). A value number identifies the maximal set
    // of segments of one vreg carrying the SAME value — segments connected through
    // the CFG with no intervening def. A def starts a new value number; at a CFG
    // join the inflowing value numbers unify into one. Nothing reads ValNo yet —
    // these tests pin the tagging the later SplitKit stages will consume.
    // =========================================================================

    // VN-1. Straight-line single def: one value number across the whole range.
    //
    //   %0 = arith.constant 1   ; i0 — def %0
    //   %1 = arith.addi %0, %0  ; i1 — use of %0
    //   %2 = pseudo.copy %0     ; i2 — use of %0 again (still the same value)
    [Test]
    public async Task ValNo_StraightLineSingleDef_OneValueNumber()
    {
        var fn = new MirFunction("vn_straight", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v2, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        var seg = li.IntervalOf(v0).Segments;
        // One continuous segment, one value number.
        await Assert.That(seg.Count).IsEqualTo(1);
        await Assert.That(seg[0].ValNo).IsGreaterThanOrEqualTo(0); // a real (non-sentinel) VN
    }

    // VN-2. In-block re-def with a hole: def, use, re-def, use → two distinct
    // value numbers with the hole between them.
    //
    //   %0 = arith.constant 1   ; i0 — def %0 (value A)
    //   %1 = arith.addi %0, %0  ; i1 — last use of A
    //                           ;      (hole: %0 dead)
    //   %0 = arith.constant 3   ; i2 — re-def %0 (value B)
    //   %2 = arith.addi %0, %0  ; i3 — use of B
    [Test]
    public async Task ValNo_InBlockRedefWithHole_TwoValueNumbers()
    {
        var fn = new MirFunction("vn_hole", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v1, true), new VirtualReg(v0, false), new VirtualReg(v0, false));
        var i2 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(3));
        var i3 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v2, true), new VirtualReg(v0, false), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        var seg = li.IntervalOf(v0).Segments;
        await Assert.That(seg.Count).IsEqualTo(2);
        // Distinct value numbers; the two segments do NOT merge despite both being
        // the same vreg — the re-def started a new value.
        await Assert.That(seg[0].ValNo).IsNotEqualTo(seg[1].ValNo);
        // Each value number is anchored at its own def slot.
        await Assert.That(seg[0]).IsEqualTo(
            new LiveSegment(DefPt(li, i0), DefPt(li, i1), seg[0].ValNo));
        await Assert.That(seg[1]).IsEqualTo(
            new LiveSegment(DefPt(li, i2), DefPt(li, i3), seg[1].ValNo));
    }

    // VN-3. Two-address tied through-segment: a vreg used AND re-defined by the
    // SAME instruction (a tied operand) runs *through* the instruction. The tied
    // re-def starts a new value number at its def slot, with a continuous segment
    // (no spurious hole) across the boundary.
    //
    //   %0 = arith.constant 1       ; i0 — def %0 (value A)
    //   %0 = arith.addi %0, %1      ; i1 — %0 tied: read (A) then written (B)
    //   %2 = pseudo.copy %0         ; i2 — use of B
    [Test]
    public async Task ValNo_TwoAddressTied_NewValueNumberAtDef_NoHole()
    {
        var fn = new MirFunction("vn_tied", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);
        var v2 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v1, true), new Immediate(2));
        // %0 both used and re-defined here (two-address / tied form).
        var i1 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI),
            new VirtualReg(v0, true), new VirtualReg(v0, false), new VirtualReg(v1, false));
        var i2 = bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v2, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        var seg = li.IntervalOf(v0).Segments;
        // Two value numbers: A = [def i0 .. def i1), B = [def i1 .. def i2).
        await Assert.That(seg.Count).IsEqualTo(2);
        await Assert.That(seg[0].ValNo).IsNotEqualTo(seg[1].ValNo);
        // The boundary is the tied instruction's def slot — and the two segments
        // ABUT there (seg0.End == seg1.Start == def i1): the value runs through
        // the instruction with NO hole. (They stay two segments only because of
        // the value-number change, not because of a gap.)
        await Assert.That(seg[0].End).IsEqualTo(DefPt(li, i1));
        await Assert.That(seg[1].Start).IsEqualTo(DefPt(li, i1));
        await Assert.That(seg[0].Start).IsEqualTo(DefPt(li, i0));
        await Assert.That(seg[1].End).IsEqualTo(DefPt(li, i2));
        // No uncovered point between them: covering is continuous across i1's def.
        await Assert.That(li.IntervalOf(v0).Covers(DefPt(li, i1))).IsTrue();
    }

    // VN-4. Cross-block live-out / live-in (no join): def in A, use in successor B
    // → ONE value number spanning the edge (the live-in value in B is the value
    // defined in A, carried across the edge).
    //
    //   bb0: %0 = arith.constant 1 ; i0 — def %0 (value A)
    //        cf.br bb1
    //   bb1: %1 = pseudo.copy %0   ; i1 — use of A in the successor
    [Test]
    public async Task ValNo_CrossBlockNoJoin_OneValueNumber()
    {
        var fn = new MirFunction("vn_xblock", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        var i0 = bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb1, []));
        bb1.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        var seg = li.IntervalOf(v0).Segments;
        // One continuous segment across the edge, so necessarily one value number.
        await Assert.That(seg.Count).IsEqualTo(1);
        await Assert.That(seg[0].Start).IsEqualTo(DefPt(li, i0));
        await Assert.That(seg[0].ValNo).IsGreaterThanOrEqualTo(0);
        // The value number flowing into bb1 is the def's value number — the carry.
        await Assert.That(li.IntervalOf(v0).Segments[0].ValNo).IsGreaterThanOrEqualTo(0);
    }

    // VN-5. CFG join (the union-find case): a diamond where the vreg is defined on
    // BOTH arms and used at the join. The two arm-defs' values must UNIFY into ONE
    // value number at the join's live-in segment — proving the cross-block carry.
    //
    //   bb0:  cf.cond_br %c, bb1, bb2
    //   bb1:  %0 = arith.constant 1   ; def %0 (value A)   → bb3
    //   bb2:  %0 = arith.constant 2   ; def %0 (value B)   → bb3
    //   bb3:  %1 = pseudo.copy %0     ; use of %0 (merged value at the join)
    [Test]
    public async Task ValNo_CfgJoin_ArmDefsUnifyToOneValueNumber()
    {
        var fn = new MirFunction("vn_join", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        var bb1 = fn.CreateBlock();
        var bb2 = fn.CreateBlock();
        var bb3 = fn.CreateBlock();
        var c = fn.CreateVirtualRegister(IRType.I1);
        var v0 = fn.CreateVirtualRegister(IRType.I8);
        var v1 = fn.CreateVirtualRegister(IRType.I8);

        bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(c, true), new Immediate(1));
        bb0.AddInstruction(CfDialect.OpRef(CfOp.CondBr),
            new VirtualReg(c, false),
            new BlockTarget(bb1, []),
            new BlockTarget(bb2, []));

        var armA = bb1.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(1));
        bb1.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb3, []));

        var armB = bb2.AddInstruction(ArithDialect.OpRef(ArithOp.Constant),
            new VirtualReg(v0, true), new Immediate(2));
        bb2.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(bb3, []));

        var join = bb3.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v1, true), new VirtualReg(v0, false));

        var li = Analysis.Compute(fn);

        // %0 has three segments: the two arm defs and the join's live-in segment.
        // The join's segment (the one covering the use at bb3) must carry a value
        // number EQUAL to BOTH arm-def value numbers — the union-find unified them.
        var iv = li.IntervalOf(v0);
        int ValNoCovering(int point)
        {
            foreach (var s in iv.Segments)
                if (s.Contains(point)) return s.ValNo;
            throw new InvalidOperationException($"no segment covers {point}");
        }

        var armAValNo = ValNoCovering(DefPt(li, armA));
        var armBValNo = ValNoCovering(DefPt(li, armB));
        var joinValNo = ValNoCovering(UsePt(li, join));

        // The two arm definitions and the merged join value are ONE value number.
        await Assert.That(armAValNo).IsEqualTo(armBValNo);
        await Assert.That(joinValNo).IsEqualTo(armAValNo);
    }
}
