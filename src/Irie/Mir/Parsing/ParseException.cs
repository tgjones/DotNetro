namespace Irie.Mir.Parsing;

public sealed class ParseException(Diagnostic diagnostic)
    : Exception(diagnostic.ErrorMessage)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = [diagnostic];
}
