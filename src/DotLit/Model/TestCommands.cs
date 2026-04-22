namespace DotLit.Model;

internal abstract record TestCommand();

internal sealed record RunCommand(string CommandLine) : TestCommand();

internal sealed record CheckCommand(string Pattern) : TestCommand();
