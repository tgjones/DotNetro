using Irie.Mir;

namespace Irie.Passes.Analyses;

// =============================================================================
// LiveIntervals — physreg-aware live-interval analysis with holes.
// =============================================================================
//
// This is the *result* type produced by LiveIntervalsAnalysis. It is the
// foundation the redesigned register allocator (see
// notes/register-allocator-redesign-plan.md §3.1) builds on. Appel, *Modern
// Compiler Implementation* ch. 11, is the reference for the terminology
// ("live range", "interference", "interval"); this file follows it.
//
// What "live interval" means
// ---------------------------
// A *value* (a vreg, or a value sitting in a physreg) is *live* at a program
// point if it has already been defined and will still be read in the future.
// The set of points where a value is live, taken together, is its *live range*.
// We represent that range as a *list of segments* — half-open [start, end)
// windows over the global instruction numbering — rather than a single
// [start, end] pair. The list form is what lets us model *holes*: a value can
// be live, go dead (its last use passes), then become live again at a later
// re-definition. Tracking holes is what makes interference *exact*: two values
// that are each live in disjoint windows do not actually interfere even if
// their outermost [min,max] ranges overlap.
//
// Interference
// ------------
// Two intervals *interfere* iff any segment of one overlaps any segment of the
// other. Overlap is the half-open test `a.Start < b.End && b.Start < a.End`.
// A precoloured physreg interval interfering with a vreg interval means the
// vreg cannot be assigned that physreg.
//
// -----------------------------------------------------------------------------
// Instruction numbering — def/use sub-slots (a Phase-1 design decision)
// -----------------------------------------------------------------------------
// The old LivenessAnalysis numbered one slot per instruction. That is too
// coarse to express the most valuable sharing on this target: a value and its
// single consumer occupying the *same* physreg (e.g. a value computed in $a and
// immediately added in $a). With one slot per instruction, the producer's range
// ends at slot N and the consumer's range starts at slot N, so a half-open
// overlap test cannot tell "consumer reads exactly where producer dies" apart
// from "both genuinely overlap".
//
// We therefore split each instruction into TWO sub-slots, mirroring LLVM's
// SlotIndexes (which uses register/dead/early-clobber sub-slots):
//
//     base = SlotsPerInstruction * instructionIndex
//     UsePoint(base) = base            // operands are READ here ("early")
//     DefPoint(base) = base + 1        // results are WRITTEN here ("late")
//
// A use's live segment ends at the instruction's *use point*; a def's live
// segment starts at the instruction's *def point*. So a value used at
// instruction N (segment ending at base_N) and a fresh value defined at the same
// instruction N (segment starting at base_N + 1) do NOT overlap — the half-open
// windows [.., base_N) and [base_N+1, ..) are disjoint. That is exactly the
// def/use sharing the single-slot scheme could not express. (Plan §5,
// "Instruction numbering granularity"; plan §7 Phase 1 flags this as the call to
// make here.)
//
// A value that is BOTH used and re-defined by the same instruction (a tied
// operand: read at the use point, written back at the def point) has a segment
// that runs *through* the instruction — it ends at the def point of the
// re-defining instruction, not the use point — so a tied def/use is one
// continuous live segment, as it must be.
public sealed record LiveIntervals(
    // Sub-slot numbering. Each instruction owns SlotsPerInstruction consecutive
    // integer points; UseSlotOf / DefSlotOf below derive the two sub-slots.
    IReadOnlyDictionary<MirInstruction, int> BaseSlotOf,

    // Per-vreg live interval (segments + interference queries).
    IReadOnlyDictionary<int, LiveInterval> VRegIntervals,

    // Per-physreg live interval, derived from CC live-ins, explicit physreg
    // operands, and implicit-def/use physreg operands (call clobbers, flag
    // defs). Keyed by physical-register id.
    IReadOnlyDictionary<int, LiveInterval> PhysRegIntervals)
{
    // Two sub-slots per instruction: the use ("early") point and the def
    // ("late") point. See the header comment for why.
    public const int SlotsPerInstruction = 2;

    // The point at which an instruction READS its use operands.
    public static int UseSlot(int baseSlot) => baseSlot;

    // The point at which an instruction WRITES its def operands.
    public static int DefSlot(int baseSlot) => baseSlot + 1;

    // ---- Query API (consumed by RegisterAllocatorPass in Phase 2) ----------

    // Does this vreg's interval overlap this physreg's busy interval? True =>
    // the vreg cannot be placed on that physreg. Subsumes the old pass's
    // IsClobberFree / IsReservationFree (a clobber or reservation is just a
    // segment of the physreg's interval). Returns false when either side has no
    // interval (an untracked vreg/physreg is never busy).
    public bool Overlaps(int vreg, int physReg)
    {
        if (!VRegIntervals.TryGetValue(vreg, out var v)) return false;
        if (!PhysRegIntervals.TryGetValue(physReg, out var p)) return false;
        return v.Overlaps(p);
    }

    // Do two vregs interfere (any segment overlaps)? This is the edge test of
    // the interference graph the Phase-3 coalescer/colourer will build.
    public bool Interfere(int vregA, int vregB)
    {
        if (vregA == vregB) return false;
        if (!VRegIntervals.TryGetValue(vregA, out var a)) return false;
        if (!VRegIntervals.TryGetValue(vregB, out var b)) return false;
        return a.Overlaps(b);
    }

    // The segments of a vreg's interval, or an empty interval if untracked.
    public LiveInterval IntervalOf(int vreg) =>
        VRegIntervals.TryGetValue(vreg, out var v) ? v : LiveInterval.Empty;

    // The segments of a physreg's interval, or an empty interval if untracked.
    public LiveInterval PhysIntervalOf(int physReg) =>
        PhysRegIntervals.TryGetValue(physReg, out var p) ? p : LiveInterval.Empty;

    // The value number of the segment of `vreg` that COVERS `slot`, or null if no
    // segment covers it (the vreg is not live across that point). SplitKit (S2)
    // uses this to identify which value of a vreg is live at a split point — the
    // value the split relocates. See LiveSegment's ValNo doc.
    public int? ValNoAt(int vreg, int slot)
    {
        if (!VRegIntervals.TryGetValue(vreg, out var v)) return null;
        foreach (var s in v.Segments)
            if (s.Contains(slot)) return s.ValNo;
        return null;
    }
}

// A half-open live window [Start, End) over the global sub-slot numbering.
// Half-open is deliberate: it makes "ends exactly where the next thing starts"
// (def/use sharing) a non-overlap, which is the whole point of the sub-slot
// scheme. Start == End is an empty segment (never stored).
//
// ValNo — value number (SplitKit foundation, S1)
// ----------------------------------------------
// A *value number* identifies the maximal set of segments of one vreg that carry
// the SAME value: segments connected through the CFG with no intervening def of
// that vreg. A def starts a new value number; at a CFG join the value numbers
// flowing in from each predecessor are unified into one (post-PhiElimination MIR
// is non-SSA, so a vreg can legitimately have multiple defs reaching a join — the
// join's live-in is genuinely one merged value). Computed by
// LiveIntervalsValueNumbering (a reaching-definition union-find) and threaded
// onto each emitted segment. ValNo is NOT involved in interference: Overlaps /
// Interfere / Covers test slot overlap only, never ValNo. Later SplitKit stages
// (S2+) consume ValNo to split one value number out of a vreg's range; nothing
// reads it yet at S1.
//
// PHYSREG segments use the sentinel ValNo PhysRegValNo (SplitKit splits vregs,
// not physregs, so physregs need no value numbering for S1).
public readonly record struct LiveSegment(int Start, int End, int ValNo)
{
    // Sentinel value number for segments that are not value-numbered (physregs).
    public const int PhysRegValNo = -1;

    public bool OverlapsWith(LiveSegment other) =>
        Start < other.End && other.Start < End;

    public bool Contains(int point) => Start <= point && point < End;
}

// A value's live range: a list of disjoint, ascending segments (holes between
// them). Built by Add()-ing raw segments and then Normalize()-ing (sort + merge
// touching/overlapping segments). After Normalize the list is canonical, so two
// intervals over the same numbering compare/overlap deterministically.
public sealed class LiveInterval
{
    public static readonly LiveInterval Empty = new();

    private readonly List<LiveSegment> _segments = [];

    public IReadOnlyList<LiveSegment> Segments => _segments;

    public bool IsEmpty => _segments.Count == 0;

    // First defined / last live point across all segments. Useful for the
    // linear-scan ordering Phase 2 will use (order by Start).
    public int Start => _segments.Count == 0 ? 0 : _segments[0].Start;
    public int End => _segments.Count == 0 ? 0 : _segments[^1].End;

    // Add a raw [start, end) window tagged with a value number. Empty/reversed
    // windows are dropped. Call Normalize() once after all Add()s before querying.
    // Physreg segments pass LiveSegment.PhysRegValNo (the sentinel).
    public void Add(int start, int end, int valNo)
    {
        if (end <= start) return;
        _segments.Add(new LiveSegment(start, end, valNo));
    }

    // Sort by start and coalesce touching or overlapping segments into maximal
    // runs — but ONLY when the two segments share the same ValNo. "Touching"
    // (prev.End == next.Start) segments merge because a value live up to a point
    // and again from that same point has no real hole there; however two abutting
    // segments with DIFFERENT value numbers must stay distinct (that is the whole
    // point of value numbers — SplitKit splits one value number out of a range).
    // Overlapping segments of the same ValNo still merge. Physreg segments all
    // carry the sentinel PhysRegValNo, so they merge on adjacency exactly as
    // before — preserving physreg interference behaviour.
    public void Normalize()
    {
        if (_segments.Count <= 1) return;
        _segments.Sort(static (a, b) =>
            a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var merged = new List<LiveSegment>(_segments.Count);
        var current = _segments[0];
        for (var i = 1; i < _segments.Count; i++)
        {
            var s = _segments[i];
            // Merge only same-value-number neighbours. Different ValNo abutting
            // segments stay separate even though they touch/overlap.
            if (s.Start <= current.End && s.ValNo == current.ValNo) // same value → extend
                current = current with { End = Math.Max(current.End, s.End) };
            else
            {
                merged.Add(current);
                current = s;
            }
        }
        merged.Add(current);

        _segments.Clear();
        _segments.AddRange(merged);
    }

    // True if any segment of this interval overlaps any segment of the other.
    // Both intervals are normalized (ascending, disjoint), so we can sweep the
    // two segment lists in lock-step — a standard two-pointer merge — in linear
    // time rather than the quadratic all-pairs comparison.
    public bool Overlaps(LiveInterval other)
    {
        var i = 0;
        var j = 0;
        var a = _segments;
        var b = other._segments;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].OverlapsWith(b[j])) return true;
            // Advance whichever segment ends first; it cannot overlap any later
            // (higher-start) segment of the other list.
            if (a[i].End <= b[j].End) i++;
            else j++;
        }
        return false;
    }

    // True if any segment covers the given point. Used by the allocator to ask
    // "is this physreg busy at exactly this slot?".
    public bool Covers(int point)
    {
        foreach (var s in _segments)
            if (s.Contains(point)) return true;
        return false;
    }
}
