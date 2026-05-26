namespace Irie.Mir;

public readonly record struct DialectId(int Index);

public readonly record struct OpcodeRef(DialectId Dialect, ushort Code);
