namespace Irie.Mir.Parsing;

public sealed record Diagnostic(int Line, int Column, string ErrorMessage);
