namespace Irie.Mir;

// A reservation for a per-function stack-substitute region in static memory.
// The unified-MIR pipeline places address-taken locals (and value-type locals)
// into named .bss-style globals rather than a real call stack — see plan §2.6.
// `FrameLoweringPass` materialises each FrameSlot as a MirGlobal once the
// function is fully built.
public sealed record FrameSlot(int Index, IRType Type, string SymbolName)
{
    // The slot's target stack id (the llvm-mos `TargetStackID` analogue) and its
    // offset within that stack (the `getObjectOffset` analogue). Both are opaque
    // integers decided by the frame-placement pass; generic code never interprets
    // a non-default StackId — only the owning target knows what a given id means.
    // `DefaultStackId` (0) is the universal default: an absolute-memory global.
    // Neither field is serialised in MIR text/binary (a pass recomputes them).
    public int StackId { get; set; } = DefaultStackId;
    public int Offset { get; set; }

    // The default stack id: the slot lives in an absolute-memory .bss-style global.
    public const int DefaultStackId = 0;
}
