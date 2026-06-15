namespace Irie.Mir;

// Module-level named region of memory: string constants, static fields,
// vtables, and frame slots are all instances of this single concept.
// `Initializer` is null for zero-init (`.bss`-style) globals.
public sealed record MirGlobal(
    string          SymbolName,
    IRType          Type,
    int             SizeInBytes,
    MirInitializer? Initializer)
{
    // When non-null, the global is a *fixed zero-page reservation* at this byte
    // address (an `AS_ZeroPage` analogue from llvm-mos): the binary encoder
    // records the symbol at this address but emits no bytes and does not advance
    // the absolute cursor. `null` (the default) = an absolute-memory global,
    // laid out after all functions exactly as today.
    public int? ZeroPageAddress { get; init; }
}

// Sequence of data items concatenated to form a global's initial value.
public sealed record MirInitializer(MirDataItem[] Items);

// A single item inside a MirInitializer.
public abstract record MirDataItem;

// Raw bytes (e.g. a string's ASCII payload).
public sealed record DataBytes(byte[] Bytes) : MirDataItem;

// A 2-byte little-endian reference to another global's address (e.g. a vtable
// slot pointing at a method). Resolved at link time.
public sealed record DataSymbolRef(string SymbolName) : MirDataItem;
