using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private post-RA pass that sequentializes maximal runs of
// physreg→physreg `pseudo.copy` instructions as a *parallel copy*.
//
// Why this is needed: the call-argument setup emitted by MOS6502CallLowering is
// a straight-line sequence of `pseudo.copy $physreg <- %argByteVreg`. After RA
// assigns homes, a destination can collide with a *later* copy's source (RA
// parked that source in a register this copy overwrites), so executing the run
// top-to-bottom would read an already-clobbered value. This pass treats each
// maximal run as a parallel copy {dst_i <- src_i} and re-orders it so every
// source is read before it is overwritten, breaking genuine permutation cycles
// with a fresh zero-page temp (mirrors PhiEliminationPass).
//
// This pass is PURELY about that value-ordering. It does NOT reason about $a as a
// hidden scratch register: copies that need scratch to lower (zp→zp, $x↔$y) are
// re-emitted as scratch forms by MOS6502PseudoExpander and resolved by the
// post-expansion RegisterScavengingPass, which picks a location DEAD at the
// copy's point — so a live $a (or any live value) is never trashed. Keeping the
// two concerns separate (value sequencing here, scratch scavenging there) is the
// single-mechanism design; the old $a-evacuation logic that used to live here is
// gone.
//
// Cycle-break temporaries are drawn from zero-page slots dead at the run's
// program point: any slot live *out* of the run (read by a later instruction —
// e.g. a jsr's implicit arg uses — before being redefined, or live into a
// successor block) is excluded, so a temp never clobbers a value still in flight.
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

                // A lone copy is trivially in order; only multi-copy runs can have
                // a read-after-overwrite hazard.
                if (copies.Count <= 1) continue;

                // If executing the run top-to-bottom already reads every source
                // before it is overwritten, leave it untouched (avoids pessimizing
                // the many already-correct copy runs RA emits).
                if (NaturalOrderIsSafe(copies)) continue;

                // Resolve the run's net effect into a parallel copy
                // {dst <- originalSource} by symbolically simulating it, then
                // re-sequentialize that so every source is read before it is
                // overwritten. Chains collapse; genuine value cycles are broken by
                // Sequentialize's scratch-temp path.
                var parallel = ResolveToParallelCopy(copies);
                if (parallel.Count == 0) continue; // run was a net no-op

                // Zero-page slots dead after the run, so a cycle-break temp never
                // clobbers a value still needed.
                var liveOut = LiveOutAfterRun(block, runEnd: i);
                var scheduled = new List<(int dst, int src)>();
                Sequentialize(parallel, scheduled, new ScratchAllocator(parallel, liveOut));

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
    // parallel-copy semantics: a copy must not read a register whose value was
    // already overwritten by an earlier copy in the run (its home reused as some
    // earlier copy's destination). When none does, the run executes correctly in
    // its emitted order and needs no rescheduling. (Hidden-$a-scratch hazards are
    // NOT considered here — they are handled downstream by the scavenger.)
    private static bool NaturalOrderIsSafe(List<(int dst, int src)> copies)
    {
        var written = new HashSet<int>(); // registers whose entry value is gone
        foreach (var (dst, src) in copies)
        {
            if (dst == src) continue;
            if (written.Contains(src)) return false; // reads an already-overwritten reg
            written.Add(dst);
        }
        return true;
    }

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

    // Classic parallel-copy sequentializer: repeatedly emit any copy whose
    // destination is not still needed as a source; when none qualifies a cycle
    // remains, broken by routing one element's source through a fresh scratch zp
    // slot (mirrors PhiEliminationPass). Scratch needs of the resulting
    // physreg→physreg copies (zp→zp, $x↔$y) are resolved later by the scavenger.
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
