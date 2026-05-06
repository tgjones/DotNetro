namespace Irie.CodeGen.Analyses;

public readonly record struct LiveRange(int Start, int End);

public sealed record Liveness(
    // Slot index assigned to each instruction (monotone across the function).
    IReadOnlyDictionary<MachineInstruction, int> SlotOf,

    // Per-vreg live range [Start, End] inclusive.
    // Block-crossing vregs get a single conservative range (no hole tracking).
    IReadOnlyDictionary<int, LiveRange> RangeOf,

    // Vregs live at the start / end of each block (used to widen RangeOf).
    IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> LiveIn,
    IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> LiveOut);