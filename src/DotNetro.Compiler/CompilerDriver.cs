using System.Collections.ObjectModel;

using Sixty502DotNet.Shared;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    public static CompilationResult Compile(string dotNetAssemblyPath, string entryPointMethodName)
    {
        var assemblyCode = DotNetCompiler.Compile(dotNetAssemblyPath, entryPointMethodName, null);

        var assemblerOutput = Assemble(assemblyCode);

        return new CompilationResult(assemblyCode, assemblerOutput.Listing, assemblerOutput.CompiledProgram, assemblerOutput.CompiledImage);
    }

    private static AssemblerResult Assemble(string assemblyCode)
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

        var listing = string.Join(System.Environment.NewLine, state.StatementListings);

        var objectBytes = state.Output.GetCompilation();

        var outputFormatInfo = new OutputFormatInfo("foo", state.Output.ProgramStart, objectBytes);

        return new AssemblerResult(
            listing,
            objectBytes,
            state.Output.OutputFormat!.GetFormat(outputFormatInfo));
    }

    private sealed record AssemblerResult(string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);