namespace DotLit.Model;

public abstract record TestCommand();

public sealed record RunCommand(string CommandLine) : TestCommand();

public sealed record CheckCommand(string Pattern) : TestCommand();
