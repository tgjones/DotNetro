namespace Irie.IR.Binary;

internal static class IRBinaryFormat
{
    internal enum TypeTag : byte { Void, I8, I16, I32 }
    internal enum OpcodeTag : byte { IntegerLiteral, IntegerAdd, Return }
}
