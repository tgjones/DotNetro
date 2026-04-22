namespace DotLit.Model;

internal sealed record TestFile(string FilePath, TestCommand[] Commands);
