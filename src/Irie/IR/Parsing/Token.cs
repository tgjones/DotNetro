namespace Irie.IR.Parsing;

internal record Token(TokenKind Kind, int Line, int Column, string? Text = null, long? IntValue = null);
