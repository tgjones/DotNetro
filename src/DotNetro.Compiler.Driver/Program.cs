using System.CommandLine;
using DotNetro.Compiler;

var assemblyOption = new Option<FileInfo?>("--assembly") { Description = "Path to the assembly to compile." };
var outputOption = new Option<FileInfo?>("--output") { Description = "Path to the output file." };

var rootCommand = new RootCommand();
rootCommand.Options.Add(assemblyOption);
rootCommand.Options.Add(outputOption);

rootCommand.SetAction(parseResult =>
{
    var assembly = parseResult.GetValue(assemblyOption);
    var output = parseResult.GetValue(outputOption);

    var compilationResult = CompilerDriver.Compile(assembly!.FullName, "Main");
    File.WriteAllBytes(output!.FullName, [.. compilationResult.CompiledImage]);

    File.WriteAllText(Path.ChangeExtension(output!.FullName, ".asm"), compilationResult.AssemblyCode);
    File.WriteAllText(Path.ChangeExtension(output!.FullName, ".lst"), compilationResult.Listing);
});

return await rootCommand.Parse(args).InvokeAsync();