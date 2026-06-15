namespace Irie.Mir;

// Module-level named region of memory: string constants, static fields,
// vtables, and frame slots are all instances of this single concept.
// `Initializer` is null for zero-init (`.bss`-style) globals.
public sealed record MirGlobal(
    string          SymbolName,
    IRType          Type,
    int             SizeInBytes,
    MirInitializer? Initializer);

// Sequence of data items concatenated to form a global's initial value.
public sealed record MirInitializer(MirDataItem[] Items);

// A single item inside a MirInitializer.
public abstract record MirDataItem;

// Raw bytes (e.g. a string's ASCII payload).
public sealed record DataBytes(byte[] Bytes) : MirDataItem;

// A 2-byte little-endian reference to another global's address (e.g. a vtable
// slot pointing at a method). Resolved at link time.
public sealed record DataSymbolRef(string SymbolName) : MirDataItem;
