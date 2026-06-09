using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private post-RA pass that schedules maximal runs of physreg→physreg
// `pseudo.copy` instructions as a *parallel copy*.
//
// Why this is needed: the call-argument setup emitted by MOS6502CallLowering is
// a straight-line sequence of `pseudo.copy $physreg <- %argByteVreg`. After RA
// assigns homes these become physreg→physreg copies that run sequentially, but
// on the 6502 several of those moves clobber $a as a hidden scratch:
//   * a zero-page → zero-page move expands to `LDA src ; STA dst`  (clobbers $a)
//   * an $x ↔ $y move routes through $a                            (clobbers $a)
// If the copy whose *destination* is $a is emitted before a later zp→zp copy,
// that later copy overwrites $a and the argument is lost. CC_MOS passes a wide
// value LSB-first in $a, $x, RC2, RC3, …, so a 32-bit argument always hits this.
//
// This pass treats each maximal contiguous run of physreg→physreg `pseudo.copy`
// as a parallel copy {dst_i <- src_i} and, when the emitted order is wrong under
// the $a-scratch hazard, re-orders it into a correct schedule that:
//   1. preserves read-before-overwrite ordering;
//   2. emits the copy that establishes $a's final value (dst == $a) LAST, after
//      every copy that clobbers $a (zp→zp, $x↔$y); and
//   3. evacuates any live $a value, or a $a-destined copy's about-to-be-overwritten
//      source, into a free scratch zero-page slot first.
//
// Scope: only *acyclic* parallel copies are rescheduled (see IsAcyclicParallelCopy
// for why permutation cycles are deliberately left untouched — they are
// indistinguishable from sequential save/restore idioms). The reported call-arg
// clobber is always acyclic, so this covers it. (The generic cycle-break in
// Sequentialize is kept as defensive code but is unreachable given that scope.)
//
// $a-evacuation temporaries are drawn from zero-page slots that are dead at the
// run's program point: any slot live *out* of the run (read by a later
// instruction — e.g. a jsr's implicit arg uses — before being redefined, or live
// into a successor block) is excluded, so a temp never clobbers a value still in
// flight.
//
// Runs between RA/CopyElimination and PseudoExpansion (the operands are physregs
// only by now). Order relative to MOS6502AddressingModeSelectorPass is
// irrelevant — AMS never touches `pseudo.copy`.
public sealed class MOS6502ParallelCopyPass : MirFunctionPass
{
    public override string Name => "MOS6502ParallelCopy";

    public override void Run(MirFunction function)
    {
        function.RebuildCfg();

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count;)
            {
                if (!IsPhysRegCopy(block.Instructions[i], out _, out _))
                {
                    i++;
                    continue;
                }

                // Found the start of a maximal run; gather it.
                var runStart = i;
                var copies = new List<(int dst, int src)>();
                while (i < block.Instructions.Count &&
                       IsPhysRegCopy(block.Instructions[i], out var dst, out var src))
                {
                    copies.Add((dst, src));
                    i++;
                }

                if (copies.Count == 0) continue;

                // A lone copy never needs *reordering*, but a single zp→zp / $x↔$y
                // copy still trashes $a as a hidden scratch — a hazard when $a is
                // live across it (case (d)). Keep the fast path for the common
                // lone non-clobbering copy; everything else falls through to the
                // liveness-aware safety check below.
                if (copies.Count == 1 && !ClobbersAccumulator(copies[0].dst, copies[0].src))
                    continue;

                // Physregs live just after the run — needed both to detect the
                // "$a live past the run" hazard and (later) so a cycle-break /
                // $a-save never clobbers a value still needed.
                var liveOut = LiveOutAfterRun(block, runEnd: i);

                // If the emitted order already executes correctly under the 6502
                // $a-scratch hazard, leave it untouched — no need to reschedule
                // (avoids pessimizing the many already-correct copy runs RA emits).
                if (NaturalOrderIsSafe(copies, liveOut)) continue;

                // A post-RA contiguous copy run is a *sequential* copy list: each
                // copy reads the current register state, so a source that equals an
                // earlier destination is an ordinary forward data dependence, not a
                // permutation cycle. (PhiElimination has already broken any real
                // value cycle with a temp; a contiguous `$zpN<-$a ; $a<-$zpN` save/
                // restore pair without an intervening clobber is simply a no-op on
                // $a.) Resolve the run's net effect into a parallel copy
                // {dst <- originalSource} by symbolically simulating it, then let the
                // classic parallel-copy scheduler reschedule that — correctly
                // dodging the $a-scratch hazard while preserving every register's
                // final value. Chains collapse, genuine value cycles (if any survive)
                // are broken by Schedule's scratch-temp path.
                var parallel = ResolveToParallelCopy(copies);
                if (parallel.Count == 0) continue; // run was a net no-op

                var scheduled = Schedule(parallel, liveOut);

                // Replace the run in-place with the scheduled copies.
                block.Instructions.RemoveRange(runStart, copies.Count);
                for (var k = 0; k < scheduled.Count; k++)
                {
                    var (dst, src) = scheduled[k];
                    block.InsertInstruction(runStart + k,
                        PseudoDialect.OpRef(PseudoOp.Copy),
                        new PhysicalReg(dst, IsDefinition: true),
                        new PhysicalReg(src, IsDefinition: false));
                }

                // Continue scanning after the freshly inserted run.
                i = runStart + scheduled.Count;
            }
        }
    }

    // True if executing the copies in their emitted order already realises the
    // parallel-copy semantics under the 6502 $a-scratch hazard. Three ways the
    // emitted order can be wrong:
    //   (a) a copy reads a register whose value was already overwritten (its home
    //       was reused by an earlier copy);
    //   (b) a copy reads $a after $a's entry value was displaced (written, or
    //       trashed as scratch by an earlier zp→zp / $x↔$y move);
    //   (c) the value a copy writes into $a is clobbered before the run ends — the
    //       run's $a output is consumed *after* the run (by the following jsr/rts),
    //       so any clobbering copy after a `dst == $a` write corrupts it.
    //   (d) $a's *entry* value is live past the run (some later instruction reads
    //       $a) but no copy re-establishes it, and a copy trashes $a as a hidden
    //       scratch (zp→zp / $x↔$y). The later read then sees a corrupted $a.
    //       This is the case RA itself can't see: it routes an i16 high byte
    //       through $x↔$y while the low byte still lives in $a, not knowing the
    //       copy clobbers $a. Rescheduling save/restores $a around the run.
    // When none occur the run needs no rescheduling.
    private static bool NaturalOrderIsSafe(List<(int dst, int src)> copies, HashSet<int> liveOut)
    {
        var a = MOS6502Registers.A;

        // (d): $a's entry value outlives the run, no copy redefines $a, yet a copy
        // trashes $a as scratch. Unsafe regardless of intra-run ordering.
        var aWrittenByCopy = copies.Any(c => c.dst != c.src && c.dst == a);
        var aTrashedAsScratch = copies.Any(c => c.dst != c.src && ClobbersAccumulator(c.dst, c.src));
        if (liveOut.Contains(a) && !aWrittenByCopy && aTrashedAsScratch)
            return false;

        var written = new HashSet<int>(); // registers whose entry value is gone
        var aEntryGone = false;           // $a no longer holds its entry value
        var aHoldsOutput = false;         // $a currently holds a run-output value

        foreach (var (dst, src) in copies)
        {
            if (dst == src) continue;

            // (a)/(b): the copy reads `src`; its entry value must still be intact.
            if (src == a)
            {
                if (aEntryGone) return false;
            }
            else if (written.Contains(src))
            {
                return false;
            }

            var clobbersA = ClobbersAccumulator(dst, src);

            // (c): a clobber after $a was given its final run-output value.
            if (aHoldsOutput && (clobbersA || dst == a)) return false;

            written.Add(dst);
            if (dst == a)
            {
                aEntryGone = true;
                aHoldsOutput = true;
            }
            else if (clobbersA)
            {
                aEntryGone = true;
                aHoldsOutput = false; // $a now holds scratch, not a run output
            }
        }
        return true;
    }

    // True if expanding `dst <- src` (per MOS6502PseudoExpander) clobbers $a as a
    // hidden scratch register, in addition to writing dst.
    private static bool ClobbersAccumulator(int dst, int src)
    {
        if (dst == src) return false;
        if (IsZeroPage(dst) && IsZeroPage(src)) return true;           // zp→zp: LDA;STA
        if ((dst == MOS6502Registers.X && src == MOS6502Registers.Y) ||
            (dst == MOS6502Registers.Y && src == MOS6502Registers.X))  // $x↔$y via $a
            return true;
        return false;
    }

    private static bool IsZeroPage(int physReg) => physReg >= MOS6502Registers.RC(0);

    // Computes the set of physical registers live immediately after the run
    // (i.e. at instruction index `runEnd`), via backward liveness over the tail
    // of the block. Seeds from the live-ins of every successor block (set by RA),
    // then walks the trailing instructions back to `runEnd`, killing defs and
    // gen-ing uses. Implicit defs/uses are ordinary operands by this stage, so
    // they are included automatically (e.g. a jsr's implicit arg uses).
    private static HashSet<int> LiveOutAfterRun(MirBlock block, int runEnd)
    {
        var live = new HashSet<int>();
        foreach (var succ in block.Successors)
            foreach (var r in succ.LiveIns)
                live.Add(r);

        for (var k = block.Instructions.Count - 1; k >= runEnd; k--)
        {
            foreach (var op in block.Instructions[k].Operands)
                if (op is PhysicalReg { IsDefinition: true } d)
                    live.Remove(d.Id);
            foreach (var op in block.Instructions[k].Operands)
                if (op is PhysicalReg { IsDefinition: false } u)
                    live.Add(u.Id);
        }
        return live;
    }

    // Reduces a *sequential* copy run to the equivalent *parallel* copy
    // {dst <- originalSource} that produces the same final register state.
    //
    // We symbolically simulate the run: every register starts holding its own
    // entry symbol, and each copy `dst <- src` sets dst's symbol to src's
    // current symbol. The resulting map (dst → the *entry* register whose value
    // dst ends up holding) is a parallel copy reading only entry values, so it
    // can be freely rescheduled by the classic sequentializer.
    //
    // Chains collapse automatically (`zp5<-a ; zp3<-zp5` ⇒ zp3 ends holding a's
    // entry value, so the parallel copy is `zp3 <- a`). Identity results (a
    // register that ends holding its own entry value — e.g. a contiguous
    // save/restore of $a) are dropped. Registers written then overwritten
    // contribute only their final mapping.
    private static List<(int dst, int src)> ResolveToParallelCopy(List<(int dst, int src)> copies)
    {
        // current[r] = entry register whose value r currently holds.
        var current = new Dictionary<int, int>();
        int SymOf(int r) => current.TryGetValue(r, out var s) ? s : r;

        // Preserve first-write order of destinations for stable output.
        var dstOrder = new List<int>();
        var seen = new HashSet<int>();

        foreach (var (dst, src) in copies)
        {
            current[dst] = SymOf(src);
            if (seen.Add(dst)) dstOrder.Add(dst);
        }

        var result = new List<(int dst, int src)>();
        foreach (var dst in dstOrder)
        {
            var src = current[dst];
            if (src != dst) result.Add((dst, src));
        }
        return result;
    }

    private static bool IsPhysRegCopy(MirInstruction instr, out int dst, out int src)
    {
        dst = src = -1;
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 2) return false;
        if (instr.Operands[0] is not PhysicalReg { IsDefinition: true } d) return false;
        if (instr.Operands[1] is not PhysicalReg { IsDefinition: false } s) return false;
        dst = d.Id;
        src = s.Id;
        return true;
    }

    // Converts a list of parallel physreg copies {dst_i <- src_i} into an ordered
    // sequence of sequential copies with identical effect under the 6502
    // $a-scratch hazard.
    //
    // Strategy: decouple every interaction with $a so the rest of the run is an
    // ordinary parallel copy that the classic sequentializer handles, then emit
    // the single $a-establishing write last (after all $a-clobbering moves).
    //
    //   Phase A — preamble (emitted in this order):
    //     A1. If $a is *read* by any copy (src == $a), save $a into a fresh
    //         scratch slot via a store (STA — never clobbers $a) and rewrite
    //         those reads to read the scratch slot. Done first so $a's live value
    //         is preserved before any later clobbering load.
    //     A2. Set aside the (unique) copy whose dst == $a as `deferredA`. If its
    //         source is overwritten by another copy, capture that source into a
    //         fresh scratch slot now (a load that may clobber $a — harmless,
    //         since A1 already preserved $a) and rewrite deferredA to read it.
    //   Phase B — sequentialize the remaining copies over {zp, $x, $y} with the
    //     classic ordered-emit + cycle-break-via-temp algorithm. No copy now
    //     reads $a, so every move that clobbers $a as scratch (zp→zp, $x↔$y) is
    //     harmless and cycle-break temps may live freely in zp.
    //   Phase C — emit deferredA. All $a-clobberers ran in phase B and its source
    //     was preserved, so $a receives its final value undisturbed.
    private static List<(int dst, int src)> Schedule(
        List<(int dst, int src)> copies, HashSet<int> liveOut)
    {
        var a = MOS6502Registers.A;
        var result = new List<(int dst, int src)>();
        var scratch = new ScratchAllocator(copies, liveOut);

        var working = copies.Where(c => c.dst != c.src).ToList(); // drop identities

        // A1: evacuate $a when its entry value is at risk of being disturbed and
        // is still needed — either read by a copy in the run, or live past the
        // run (some later instruction reads $a) while no copy re-establishes it.
        // $a's value is at risk when some copy writes $a (dst == $a) or trashes
        // it as a hidden scratch (zp→zp / $x↔$y). If $a is merely read and never
        // disturbed (e.g. a collapsed save/restore where $a keeps its entry
        // value), the reads can stay direct — saving would only emit a pointless
        // STA/LDA round-trip.
        var aReadByCopy = working.Any(c => c.src == a);
        var aWrittenByCopy = working.Any(c => c.dst == a);
        var aValueAtRisk = aWrittenByCopy || working.Any(c => ClobbersAccumulator(c.dst, c.src));
        // $a's entry value must survive the whole run when it's live afterwards
        // and no copy gives $a a new final value (a dst==$a copy would, handled
        // by deferredA below). Case (d) in NaturalOrderIsSafe.
        var aEntryLiveOut = liveOut.Contains(a) && !aWrittenByCopy;

        int? restoreAFrom = null;
        if ((aReadByCopy || aEntryLiveOut) && aValueAtRisk)
        {
            var savedA = scratch.Next();
            result.Add((savedA, a));   // STA $savedA — does not clobber $a.
            for (var i = 0; i < working.Count; i++)
                if (working[i].src == a)
                    working[i] = (working[i].dst, savedA);
            // Restore $a after every clobberer if a later consumer needs it.
            if (aEntryLiveOut) restoreAFrom = savedA;
        }

        // A2: set aside the deferred dst == $a copy (at most one) and preserve
        // its source if another copy overwrites it.
        (int dst, int src)? deferredA = null;
        var deferredIdx = working.FindIndex(c => c.dst == a);
        if (deferredIdx >= 0)
        {
            var (dDst, dSrc) = working[deferredIdx];
            working.RemoveAt(deferredIdx);

            if (working.Any(c => c.dst == dSrc))
            {
                var savedSrc = scratch.Next();
                result.Add((savedSrc, dSrc)); // capture before any copy overwrites dSrc.
                dSrc = savedSrc;
            }
            deferredA = (dDst, dSrc);
        }

        // Phase B: standard ordered-emit + cycle-break over the remaining copies.
        Sequentialize(working, result, scratch);

        // Phase C: the final $a-establishing write, after every clobberer.
        if (deferredA is { } d)
            result.Add(d);

        // Phase C (cont.): restore $a's entry value for a later consumer when no
        // copy redefined $a. Mutually exclusive with deferredA (which only exists
        // when a copy writes $a, i.e. aEntryLiveOut is false).
        if (restoreAFrom is int saved)
            result.Add((a, saved));   // LDA $saved — re-establish $a after clobberers.

        return result;
    }

    // Classic parallel-copy sequentializer: repeatedly emit any copy whose
    // destination is not still needed as a source; when none qualifies a cycle
    // remains, broken by routing one element's source through a fresh scratch zp
    // slot (mirrors PhiEliminationPass). Operates over locations that never
    // include a read of $a, so $a-clobbering expansions are safe here.
    private static void Sequentialize(
        List<(int dst, int src)> copies,
        List<(int dst, int src)> result,
        ScratchAllocator scratch)
    {
        var remaining = copies.Where(c => c.dst != c.src).ToList(); // drop no-ops

        while (remaining.Count > 0)
        {
            var idx = -1;
            for (var i = 0; i < remaining.Count; i++)
            {
                var dst = remaining[i].dst;
                if (!remaining.Any((o, j) => j != i && o.src == dst))
                {
                    idx = i;
                    break;
                }
            }
            if (idx >= 0)
            {
                result.Add(remaining[idx]);
                remaining.RemoveAt(idx);
                continue;
            }

            // Every pending copy's destination is still read by another: a cycle.
            // Break it by saving remaining[0]'s source into a scratch slot now,
            // then having that copy read the scratch slot later.
            var (vDst, vSrc) = remaining[0];
            var slot = scratch.Next();
            result.Add((slot, vSrc));
            remaining[0] = (vDst, slot);
        }
    }

    // Hands out distinct scratch zero-page slots, avoiding: every register that
    // appears in the run, every register live out of the run (still needed by a
    // later instruction or a successor block), and the reserved slots (RC0/RC1
    // soft-stack, RC30/RC31 indirect-call target pointer). Each slot is handed out
    // at most once per run so concurrently-live saved values never collide.
    private sealed class ScratchAllocator
    {
        private readonly HashSet<int> _used = [];
        private int _next = 2;

        public ScratchAllocator(IEnumerable<(int dst, int src)> run, HashSet<int> liveOut)
        {
            foreach (var (d, s) in run) { _used.Add(d); _used.Add(s); }
            foreach (var r in liveOut) _used.Add(r);
            _used.Add(MOS6502Registers.RC(0));
            _used.Add(MOS6502Registers.RC(1));
            _used.Add(MOS6502Registers.RC(30));
            _used.Add(MOS6502Registers.RC(31));
        }

        public int Next()
        {
            while (_next <= 29)
            {
                var rc = MOS6502Registers.RC(_next++);
                if (_used.Add(rc)) return rc;
            }
            throw new InvalidOperationException(
                "MOS6502ParallelCopyPass: no free scratch zero-page slot to break a copy cycle.");
        }
    }
}

file static class EnumerableIndexedAnyExtensions
{
    // Like Any but exposes the element index to the predicate.
    public static bool Any<T>(this IEnumerable<T> source, Func<T, int, bool> predicate)
    {
        var i = 0;
        foreach (var item in source)
        {
            if (predicate(item, i)) return true;
            i++;
        }
        return false;
    }
}
