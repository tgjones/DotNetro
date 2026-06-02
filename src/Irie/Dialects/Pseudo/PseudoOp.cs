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
}
