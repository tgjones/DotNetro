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
}
