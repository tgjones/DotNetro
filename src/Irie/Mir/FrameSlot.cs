namespace Irie.Mir;

// A reservation for a per-function stack-substitute region in static memory.
// The unified-MIR pipeline places address-taken locals (and value-type locals)
// into named .bss-style globals rather than a real call stack — see plan §2.6.
// `FrameLoweringPass` materialises each FrameSlot as a MirGlobal once the
// function is fully built.
public sealed record FrameSlot(int Index, IRType Type, string SymbolName)
{
    // Where this slot's storage lives. Decided by the frame-placement pass
    // *before* the access is lowered (the TargetStackID analogue from llvm-mos),
    // so each access is lowered correctly once rather than lowered-then-rewritten.
    // Defaults to Absolute; never serialised in MIR text (a pass recomputes it).
    public FrameSlotPlacement Placement { get; set; } = FrameSlotPlacement.Default;
}

// Where a FrameSlot's bytes are stored. `Absolute` is the .bss-style global in
// absolute RAM (today's universal default); `ZeroPage` is a fixed zero-page
// address reserved for the slot, enabling direct `lda.zp`/`sta.zp` access. The
// placement pass colours each slot into one of these; lowering keys off it (D5).
public abstract record FrameSlotPlacement
{
    // The slot lives in an absolute-memory global (indirect-Y access).
    public sealed record Absolute : FrameSlotPlacement;

    // The slot lives at a fixed zero-page byte address (direct zp access).
    public sealed record ZeroPage(int Address) : FrameSlotPlacement;

    // The default placement: absolute, matching today's universal behaviour.
    public static readonly FrameSlotPlacement Default = new Absolute();
}
