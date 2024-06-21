using System.Collections.ObjectModel;

using Sixty502DotNet.Shared;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    public static CompilationResult Compile(string dotNetAssemblyPath, string entryPointMethodName)
    {
        var assemblyCode = DotNetCompiler.Compile(dotNetAssemblyPath, entryPointMethodName, null);

        var imageBytes = Assemble(assemblyCode, out var listing);

        return new CompilationResult(assemblyCode, listing, imageBytes);
    }

    private static ReadOnlyCollection<byte> Assemble(string assemblyCode, out string listing)
    {
        var options = new Options
        {
            OutputOptions = new OutputOptions
            {
                Format = "bbcmicro",
            },
        };

        Interpreter interpreter = new(options, new FileSystemBinaryReader(""));
        AssemblyState state = interpreter.Exec(assemblyCode, new StringSourceFactory());

        foreach (var error in state.Errors)
        {
            throw error;
        }

        listing = string.Join(System.Environment.NewLine, state.StatementListings);

        return state.Output.GetCompilation();
    }
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram);