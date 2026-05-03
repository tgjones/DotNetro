namespace Irie.CodeGen;

public readonly record struct LiveRange(int Start, int End);

public sealed class Liveness(
    IReadOnlyDictionary<MachineInstruction, int> slotOf,
    IReadOnlyDictionary<int, LiveRange> rangeOf,
    IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> liveIn,
    IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> liveOut)
{
    // Slot index assigned to each instruction (monotone across the function).
    public IReadOnlyDictionary<MachineInstruction, int> SlotOf { get; } = slotOf;

    // Per-vreg live range [Start, End] inclusive.
    // Block-crossing vregs get a single conservative range (no hole tracking).
    public IReadOnlyDictionary<int, LiveRange> RangeOf { get; } = rangeOf;

    // Vregs live at the start / end of each block (used to widen RangeOf).
    public IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> LiveIn { get; } = liveIn;
    public IReadOnlyDictionary<MachineBasicBlock, HashSet<int>> LiveOut { get; } = liveOut;
}
