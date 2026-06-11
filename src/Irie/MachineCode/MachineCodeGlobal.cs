namespace Irie.MachineCode;

// Module-level named region of memory carried at the MachineCode layer.
// Mirrors Mir.MirGlobal but drops the IRType (the encoder only needs the size
// and the data items; the source-level type is a MIR concept).
public sealed record MachineCodeGlobal(
    string SymbolName,
    int SizeInBytes,
    MachineCodeDataItem[]? Items);  // null means zero-init (bss-style).

// A single piece of a global's initial value. Items are concatenated in order
// starting at the global's base address.
public abstract record MachineCodeDataItem;

// Raw bytes written verbatim at the current position within the global.
public sealed record MachineCodeDataBytes(byte[] Bytes) : MachineCodeDataItem;

// A 2-byte little-endian reference to the absolute address of another symbol
// (function or global). Resolved by the binary encoder.
public sealed record MachineCodeDataSymbolRef(string SymbolName) : MachineCodeDataItem;
