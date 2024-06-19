﻿using System.Collections.ObjectModel;

using Sixty502DotNet.Shared;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    public static void Compile(string dotNetAssemblyPath, string entryPointMethodName, string outputPath, ILogger? logger)
    {
        var assemblyCode = DotNetCompiler.Compile(dotNetAssemblyPath, entryPointMethodName, logger);

        var imageBytes = Assemble(assemblyCode, out _);

        File.WriteAllBytes(outputPath, imageBytes.ToArray());
    }

    public static ReadOnlyCollection<byte> Compile(string dotNetAssemblyPath, string entryPointMethodName, ILogger? logger)
    {
        var assemblyCode = DotNetCompiler.Compile(dotNetAssemblyPath, entryPointMethodName, logger);

        return Assemble(assemblyCode, out _);
    }

    public static ReadOnlyCollection<byte> Assemble(string assemblyCode, out string listing)
    {
        var options = new Options
        {
            ArchitectureOptions = new ArchitectureOptions
            {

            },
            DiagnosticOptions = new DiagnosticOptions
            {

            },
            GeneralOptions = new GeneralOptions
            {

            },
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
