namespace Irie.Mir.Binary;

internal static class MirBinaryFormat
{
    // ASCII "IRI3" — chosen per the unified-IR plan; the trailing digit is the
    // major format generation, not a version.
    internal static readonly byte[] Magic = [(byte)'I', (byte)'R', (byte)'I', (byte)'3'];

    internal const int FormatVersion = 1;

    internal enum TypeTag : byte
    {
        Void = 0,
        I1   = 1,
        I8   = 2,
        I16  = 3,
        I32  = 4,
    }

    internal enum OperandTag : byte
    {
        VirtualReg  = 0,
        PhysicalReg = 1,
        Immediate   = 2,
        BlockTarget = 3,
        Symbol      = 4,
    }

    internal enum VRegAnnotationTag : byte
    {
        Typed   = 0,
        Classed = 1,
    }

    internal enum DataItemTag : byte
    {
        Bytes     = 0,
        SymbolRef = 1,
    }
}
