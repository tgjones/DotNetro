namespace Irie.Dialects.Pseudo;

public enum PseudoOp : ushort
{
    // %d = pseudo.copy %s   (or $a = pseudo.copy %s, or any vreg/physreg mix).
    // The universal copy; pre-isel carries the source vreg's type, isel can
    // re-use it with a class on one side, RA coalesces/eliminates it, and
    // PseudoExpansionPass lowers any that survive to target moves.
    Copy,

    // %w = pseudo.merge %lo, %mid, …  — narrow parts → wide.
    Merge,

    // %lo, %hi = pseudo.unmerge %w   — wide → narrow parts.
    Unmerge,

    // %byte = pseudo.extract %wide, <bit_offset>
    //   Extract a sub-range from %wide starting at <bit_offset> bits. The def
    //   vreg's type determines the width of the extracted sub-range (e.g. i8
    //   def → 8-bit slice). Modelled on LLVM G_EXTRACT.
    Extract,

    // %new = pseudo.insert %wide, %sub, <bit_offset>
    //   Insert %sub into %wide at <bit_offset> bits, producing a fresh wide
    //   value. Modelled on LLVM G_INSERT.
    Insert,

    // pseudo.return   — void shell of a return after ABI lowering has copied
    // results into return physregs. Survives until instruction selection
    // lowers it to the target return op (e.g. mos6502.rts).
    Return,

    // ---- Spill / reload (register-allocator Phase 4) --------------------------
    //
    // These are the ABSTRACT spill-slot store/reload the register allocator
    // mints when it runs out of registers (notes/register-allocator-redesign-
    // plan.md §3.4). They are emitted POST register allocation, so both operands
    // are concrete physical registers; the only abstract thing is the frame-slot
    // index, which references a MirFunction.FrameSlot. RA does NOT assign that
    // slot a physical address — the static-stack placement pass
    // (StaticFramePlacementPass) colours the call graph and decides every
    // function's frame placement (zero-page vs absolute); a future spill/reload
    // lowering would consume that decision to emit each `pseudo.spill`/
    // `pseudo.reload` as a concrete `sta.zp`/`lda.zp` (zero-page frame) or a
    // `.bss` store/load. The slot shape (FrameSlot:
    // index + i8 type + symbol name) is exactly the one FrameLoweringPass and
    // that future pass already consume — see the comment on PickSpillSlot in
    // RegisterAllocatorPass for the full contract.
    //
    // pseudo.spill <slot_index>, $reg
    //   Store $reg (a USE) into the function's frame slot <slot_index>. No def.
    //   Operand shape: [Immediate(slotIndex), reg-use].
    Spill,

    // %dst = pseudo.reload <slot_index>
    //   Reload the function's frame slot <slot_index> into $dst (a DEF).
    //   Operand shape: [reg-def, Immediate(slotIndex)].
    Reload,
}
