// Takes a single argument, the path to a C# file, and compiles it to an assembly
// using Roslyn APIs. Writes the assembly to stdout.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var csFilePath = args[0];
var source = File.ReadAllText(csFilePath);

var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

var globalUsings = CSharpSyntaxTree.ParseText("""
    global using System;
    global using System.Collections.Generic;
    global using System.IO;
    global using System.Threading;
    global using System.Threading.Tasks;
    """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var references = new[]
{
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
};

var compilation = CSharpCompilation.Create(
    assemblyName: Path.GetFileNameWithoutExtension(csFilePath),
    syntaxTrees: [globalUsings, syntaxTree],
    references: references,
    options: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

using var ms = new MemoryStream();
var emitResult = compilation.Emit(ms);

if (!emitResult.Success)
{
    foreach (var diagnostic in emitResult.Diagnostics)
        Console.Error.WriteLine(diagnostic);
    return 1;
}

ms.Position = 0;
ms.CopyTo(Console.OpenStandardOutput());
return 0;
