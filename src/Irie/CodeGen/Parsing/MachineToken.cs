namespace Irie.CodeGen.Parsing;

internal record MachineToken(MachineTokenKind Kind, int Line, int Column, string? Text = null, long? IntValue = null);
