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
}
