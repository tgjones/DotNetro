namespace Irie.Mir;

// A reservation for a per-function stack-substitute region in static memory.
// The unified-MIR pipeline places address-taken locals (and value-type locals)
// into named .bss-style globals rather than a real call stack — see plan §2.6.
// `FrameLoweringPass` materialises each FrameSlot as a MirGlobal once the
// function is fully built.
public sealed record FrameSlot(int Index, IRType Type, string SymbolName);
