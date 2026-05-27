using Irie.Mir;

namespace Irie.Passes.Analyses;

public readonly record struct LiveRange(int Start, int End);

public sealed record Liveness(
    // Slot index assigned to each instruction (monotone across the function).
    IReadOnlyDictionary<MirInstruction, int> SlotOf,

    // Per-vreg live range [Start, End] inclusive.
    // Block-crossing vregs get a single conservative range (no hole tracking).
    IReadOnlyDictionary<int, LiveRange> RangeOf,

    // Vregs live at the start / end of each block (used to widen RangeOf).
    IReadOnlyDictionary<MirBlock, HashSet<int>> LiveIn,
    IReadOnlyDictionary<MirBlock, HashSet<int>> LiveOut);
