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
    // When non-null, the global is *pinned* to this pre-assigned absolute byte
    // address and reserves NO output storage: its bytes live in pre-existing RAM
    // that the target manages elsewhere, so the binary encoder records the symbol
    // at this address but emits no bytes and does not advance the output cursor.
    // `null` (the default) = an ordinary global, laid out into output storage
    // after all functions. The concept is target-agnostic (a generic
    // pinned-address reservation); a target may use it to alias a frame slot to a
    // fixed location it owns. Settable because placement may pin a global late,
    // after the global itself was materialised.
    public int? FixedAddress { get; set; }
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
