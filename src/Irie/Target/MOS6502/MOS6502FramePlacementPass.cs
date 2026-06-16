using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Target-private, post-RA placement pass — the attribute-only analogue of
// llvm-mos's MOSZeroPageAlloc (which runs in addPrePEI, post-RA, and only does
// MFI.setStackID(FI, MosZeroPage); never an address or opcode rewrite). It
// promotes the frame slots of non-reentrant functions into a fixed zero-page
// window, laid out bottom-up so a frame is disjoint from every transitive
// callee's frame (see StaticFrameColorer for the colouring model).
//
// It runs AFTER register allocation (added via AddPostRegisterAllocationPasses)
// and BEFORE the generic FrameAccessLoweringPass, so it can see spill slots
// (which only exist post-RA) and so the placement it stamps is honoured when
// MOS6502FrameLowering.LowerFrameAccess later chooses the addressing mode.
//
// Eligibility: a function is promoted iff it has frame slots and is non-reentrant.
// Address-taken slots are promoted too — there is no escape check. Faithful to
// llvm-mos's eliminateFrameIndex, a promoted slot's SYMBOL resolves to its
// zero-page address EVERYWHERE, giving it a single home: the direct frame
// load/store ops already address the zp byte via a literal (lda.zp/sta.zp #addr),
// and the escaped address materialisation (lda.imm.sym{lo,hi} @slot, passed to a
// callee) folds to that same address with no change to those ops. The pass
// achieves this by pinning the slot's global to its zero-page address — setting
// MirGlobal.FixedAddress, the generic pinned-zero-storage-reservation field —
// so the binary encoder reserves no .bss byte for the slot and resolves every
// reference to the single zero-page location. Multiple slots sharing one
// zero-page byte (the colour-reuse case) are distinct symbol names all pinned to
// the same address; the encoder permits distinct names at one address.
//
// The window is BBC-Micro-specific ($70–$8F is the portable user zero page); the
// base-chip MOS6502Target does not add this pass, so its frame slots stay
// absolute. The window therefore lives here as a private const — no generic
// Target plumbing. This is the spiritual return of the old
// MOS6502StaticFrameAllocPass, but without the RC conflation, escape check, or
// opcode rewrite, because placement now precedes addressing instead of following it.
public sealed class MOS6502FramePlacementPass : Pass
{
    public override string Name => "MOS6502FramePlacement";

    // The BBC Micro's free user zero page, dedicated to static frame storage — a
    // separate namespace from the RC imaginary register file. 32 bytes.
    private const int WindowStart = 0x70;
    private const int WindowSize = 0x20;

    public override void Run(CompilationContext context)
    {
        var module = context.Module;

        // Reentrancy gates eligibility; idempotent and cheap.
        ReentrancyAnalysis.Run(module);

        var colorer = new StaticFrameColorer(
            module.Functions,
            eligibility: f => f.FrameSlots.Count > 0 && f.IsNonReentrant,
            perFunctionFootprint: FrameSize,
            budgetSize: WindowSize);

        // Globals are materialised before this pass (FrameLoweringPass), so a slot
        // is pinned by name to its global once we know its zero-page address.
        var globalsByName = new Dictionary<string, MirGlobal>();
        foreach (var global in module.Globals)
            globalsByName[global.SymbolName] = global;

        // Colour each promoted function (its frame fits the window at its base).
        // Slot bytes go at WindowStart + base(f) + offsetWithinFrame; the slot
        // records the zero-page StackId and that absolute zero-page address as its
        // opaque Offset, so MOS6502FrameLowering needs no knowledge of the window.
        // Pinning the slot's global to that same address (FixedAddress) makes the
        // slot's symbol resolve to the zero-page byte everywhere — so an escaped
        // address folds to the very location the direct accesses use (single home),
        // and no .bss storage is reserved for it.
        foreach (var (function, baseOf) in colorer.Colour())
        {
            var within = 0;
            foreach (var slot in function.FrameSlots)
            {
                slot.StackId = MOS6502FrameStackIds.ZeroPage;
                slot.Offset = WindowStart + baseOf + within;
                if (globalsByName.TryGetValue(slot.SymbolName, out var global))
                    global.FixedAddress = slot.Offset;
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
