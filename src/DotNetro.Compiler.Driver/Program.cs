using System.CommandLine;

using DotNetro.Compiler;

var assemblyOption = new Option<FileInfo?>(name: "--assembly", description: "Path to the assembly to compile.");
var outputOption = new Option<FileInfo?>(name: "--output", description: "Path to the output file.");

var rootCommand = new RootCommand();
rootCommand.AddOption(assemblyOption);
rootCommand.AddOption(outputOption);

rootCommand.SetHandler(
    (assembly, output) =>
    {
        var compilationResult = CompilerDriver.Compile(assembly!.FullName, "Main");
        File.WriteAllBytes(output!.FullName, [.. compilationResult.CompiledImage]);

        File.WriteAllText(Path.ChangeExtension(output!.FullName, ".asm"), compilationResult.AssemblyCode);
        File.WriteAllText(Path.ChangeExtension(output!.FullName, ".lst"), compilationResult.Listing);
    }, 
    assemblyOption, 
    outputOption);

return await rootCommand.InvokeAsync(args);