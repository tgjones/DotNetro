using System.CommandLine;
using DotNetro.Compiler;

var assemblyOption = new Option<string?>("--assembly") { Description = "Path to the assembly to compile, or - for stdin" };
var outputOption = new Option<FileInfo?>("--output") { Description = "Path to the output file, or - for stdout" };
var emitOption = new Option<EmitFormat?>("--emit") { Description = "The format to emit (Assembly, Program, or Executable)", DefaultValueFactory = x => EmitFormat.Executable };

var rootCommand = new RootCommand();
rootCommand.Options.Add(assemblyOption);
rootCommand.Options.Add(outputOption);
rootCommand.Options.Add(emitOption);

rootCommand.SetAction(parseResult =>
{
    var assembly = parseResult.GetValue(assemblyOption);
    var output = parseResult.GetValue(outputOption);

    var createdTempAssembly = false;
    if (assembly == null || assembly == "-")
    {
        // Read all bytes from stdin and write them to a temporary file, then compile that file
        var tempFilePath = Path.GetTempFileName();
        using (var tempFileStream = File.OpenWrite(tempFilePath))
        {
            Console.OpenStandardInput().CopyTo(tempFileStream);
        }
        assembly = tempFilePath;
        createdTempAssembly = true;
    }

    var compilationResult = CompilerDriver.Compile(
        Path.GetFullPath(assembly),
        "Main");

    var outputStream = (output == null || output.FullName == "-")
        ? Console.OpenStandardOutput()
        : File.Create(output.FullName);

    switch (parseResult.GetValue(emitOption))
    {
        case EmitFormat.Assembly:
            using (var writer = new StreamWriter(outputStream))
            {
                writer.Write(compilationResult.AssemblyCode);
            }
            break;

        case EmitFormat.Program:
            using (var writer = new BinaryWriter(outputStream))
            {
                writer.Write(compilationResult.CompiledProgram.ToArray());
            }
            break;

        case EmitFormat.Executable:
            using (var writer = new BinaryWriter(outputStream))
            {
                writer.Write(compilationResult.CompiledImage.ToArray());
            }
            if (outputStream is FileStream)
            {
                File.WriteAllText(Path.ChangeExtension(output!.FullName, ".asm"), compilationResult.AssemblyCode);
                File.WriteAllText(Path.ChangeExtension(output!.FullName, ".lst"), compilationResult.Listing);   
            }
            break;

        default:
            throw new Exception("Emit format must be specified");
    }

    if (createdTempAssembly)
    {
        File.Delete(assembly);
    }
});

return await rootCommand.Parse(args).InvokeAsync();

enum EmitFormat
{
    Assembly,
    Program,
    Executable,
}