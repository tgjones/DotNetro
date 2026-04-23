namespace Irie.IR.Parsing;

public sealed record Diagnostic(int Line, int Column, string ErrorMessage);
