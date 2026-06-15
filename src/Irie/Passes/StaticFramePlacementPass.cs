using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// Target-agnostic, module-level pass that decides — BEFORE frame lowering — where
// each function's FrameSlots live: in a target-supplied zero-page window, or in
// absolute memory (the default). It only records the decision on each slot
// (slot.StackId / slot.Offset); FrameLoweringPass then materialises and the
// instruction selector lowers each access against the decided location. There is
// no opcode rewrite and no second pass.
//
// The static-stack colouring maths lives in the reusable StaticFrameColorer
// helper; this pass supplies the policy (eligibility, per-function footprint) and
// the target's budget, then stamps the result onto each promoted slot.
//
// Eligibility: a function may be promoted iff it has frame slots and is
// non-reentrant. No escape check and no register-collision check — the zero-page
// frame window is a separate namespace from the register file, and
// placement-before-lowering keeps every slot single-location, so neither special
// case can arise.
//
// The byte window comes from the target (Target.FreeZeroPage), threaded in via
// the constructor. The base chip target advertises FrameZeroPageWindow.None
// (Size 0), so base(f)+size(f) ≤ 0 is never true for a non-empty frame ⇒ every
// footprint is 0 ⇒ all slots stay absolute and no codegen changes. A subtarget
// with a real window promotes the frames that fit.
public sealed class StaticFramePlacementPass(FrameZeroPageWindow window) : Pass
{
    public override string Name => "StaticFramePlacement";

    private readonly FrameZeroPageWindow _window = window;

    public override void Run(CompilationContext context)
    {
        var module = context.Module;

        // Reentrancy gates eligibility; idempotent and cheap.
        ReentrancyAnalysis.Run(module);

        var colorer = new StaticFrameColorer(
            module.Functions,
            eligibility: f => f.FrameSlots.Count > 0 && f.IsNonReentrant,
            perFunctionFootprint: FrameSize,
            budgetSize: _window.Size);

        // Colour each promoted function (its frame fits the window at its base).
        // Slot bytes go at window.Start + base(f) + offsetWithinFrame; the slot
        // records the target's StackId and that absolute zero-page address as its
        // opaque Offset. Unpromoted functions keep the default placement.
        foreach (var (function, baseOf) in colorer.Colour())
        {
            var within = 0;
            foreach (var slot in function.FrameSlots)
            {
                slot.StackId = _window.StackId;
                slot.Offset = _window.Start + baseOf + within;
                within += slot.Type.SizeInBits / 8;
            }
        }
    }

    // size(f) = total bytes of f's frame slots.
    private static int FrameSize(MirFunction function)
    {
        var size = 0;
        foreach (var slot in function.FrameSlots)
            size += slot.Type.SizeInBits / 8;
        return size;
    }
}
