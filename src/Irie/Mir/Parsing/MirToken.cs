namespace Irie.Mir.Parsing;

internal sealed record MirToken(MirTokenKind Kind, int Line, int Column, string? Text = null, long? IntValue = null);
