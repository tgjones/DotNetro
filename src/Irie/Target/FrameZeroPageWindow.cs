namespace Irie.Target;

// A target's free zero-page region available to the static-frame placement pass
// as frame storage — a byte window [Start, Start+Size) disjoint from the
// register allocator's imaginary register file (the llvm-mos `zp_stack` analogue;
// see notes/mos6502-frame-placement-plan.md §3 / "Resolved decisions").
//
// `Size == 0` (the `None` default on the base Target) means the target offers no
// zero-page frame budget, so every frame slot stays in absolute memory.
public sealed record FrameZeroPageWindow(int Start, int Size)
{
    public static readonly FrameZeroPageWindow None = new(0, 0);

    // One past the last byte of the window.
    public int End => Start + Size;
}
