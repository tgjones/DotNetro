namespace Irie.Dialects.Mem;

public enum MemOp : ushort
{
    // %p : i16 = mem.symbol @name
    //   Materialise the address of a module-level global as an i16 pointer.
    //   The operand layout is: def[0] = i16 pointer vreg, use[0] = Symbol(name).
    Symbol,

    // %v : i8  = mem.load.i8  %addr
    // %v : i16 = mem.load.i16 %addr
    // %v : i32 = mem.load.i32 %addr
    //   Load a value of the indicated width from the pointer in %addr.
    //   def[0] = result vreg, use[0] = i16 pointer vreg.
    LoadI8,
    LoadI16,
    LoadI32,

    // mem.store.i8  %addr, %val
    // mem.store.i16 %addr, %val
    // mem.store.i32 %addr, %val
    //   Store %val to the pointer in %addr.
    //   use[0] = i16 pointer vreg, use[1] = value vreg.
    StoreI8,
    StoreI16,
    StoreI32,

    // Post-legalization byte forms emitted by the MOS6502 target when widening
    // mem.load / mem.store across the address. The offset is an Immediate that
    // the isel materialises into the indirect-Y addressing mode's Y register
    // (or, in the future, folds into an absolute-plus-offset addressing mode).
    //
    // %v : i8 = mem.load.byte_at %addr, <offset>
    //   def[0] = result byte vreg, use[0] = i16 pointer vreg, use[1] = Immediate offset.
    LoadByteAt,
    // mem.store.byte_at %addr, <offset>, %val
    //   use[0] = i16 pointer vreg, use[1] = Immediate offset, use[2] = i8 value vreg.
    StoreByteAt,

    // %p : i16 = mem.frame_addr <slot_index>
    //   Surface the address of the function's frame slot at the given index.
    //   def[0] = i16 pointer vreg, use[0] = Immediate slot index.
    //   FrameLoweringPass rewrites this to `mem.symbol @<func>_local<index>`.
    FrameAddr,

    // mem.fill %addr, %byte, <count>
    //   Fill <count> bytes at %addr with the byte value %byte. use[0] = i16
    //   pointer vreg, use[1] = i8 byte-pattern vreg, use[2] = Immediate count.
    //   Used by IL `initobj` (count = type size, byte = 0). The legalizer
    //   unrolls small known counts (≤ ~16) into per-byte mem.store.byte_at.
    Fill,
}
