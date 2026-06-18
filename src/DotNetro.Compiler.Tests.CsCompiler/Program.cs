// Compiles a C# file to an assembly using Roslyn APIs.
//
// Default mode: writes the emitted assembly bytes to stdout.
//
//   DotNetro.Compiler.Tests.CsCompiler <file.cs>
//
// --opcodes mode: instead of the assembly bytes, walks the IL of every method
// body in the compiled assembly and writes the distinct ILOpCode enum names it
// uses to stdout, one per line. Consumed by DotNetro.Tools.ILSupport to work
// out which IL opcodes are exercised by the lit-test corpus.
//
//   DotNetro.Compiler.Tests.CsCompiler --opcodes <file.cs>

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var emitOpcodes = false;
string? csFilePath = null;
foreach (var arg in args)
{
    if (arg == "--opcodes")
        emitOpcodes = true;
    else
        csFilePath = arg;
}

if (csFilePath is null)
{
    Console.Error.WriteLine("usage: DotNetro.Compiler.Tests.CsCompiler [--opcodes] <file.cs>");
    return 1;
}

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

if (!emitOpcodes)
{
    ms.CopyTo(Console.OpenStandardOutput());
    return 0;
}

foreach (var opCode in CollectOpcodes(ms))
    Console.WriteLine(opCode);

return 0;

// Walk every method body in the assembly and return the distinct ILOpCode
// enum names that appear, in stable sorted order.
static IEnumerable<string> CollectOpcodes(Stream assembly)
{
    var opcodes = new SortedSet<string>(StringComparer.Ordinal);

    using var peReader = new PEReader(assembly, PEStreamOptions.LeaveOpen);
    var metadata = peReader.GetMetadataReader();

    foreach (var handle in metadata.MethodDefinitions)
    {
        var method = metadata.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0)
            continue;

        var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var il = body.GetILReader();

        while (il.RemainingBytes > 0)
        {
            var opCode = ReadOpCode(ref il);
            opcodes.Add(opCode.ToString());
            SkipOperand(ref il, opCode);
        }
    }

    return opcodes;
}

static ILOpCode ReadOpCode(ref BlobReader reader)
{
    var b = reader.ReadByte();
    return b == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)b;
}

// Advance the reader past an opcode's inline operand bytes. Covers every
// ECMA-335 operand kind so the instruction stream stays in sync.
static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
{
    switch (opCode)
    {
        // No operand.
        default:
            break;

        // 1-byte operand (ShortInlineVar / ShortInlineI / ShortInlineBrTarget).
        case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s: case ILOpCode.Starg_s:
        case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s: case ILOpCode.Stloc_s:
        case ILOpCode.Ldc_i4_s: case ILOpCode.Unaligned:
        case ILOpCode.Br_s: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue_s:
        case ILOpCode.Beq_s: case ILOpCode.Bge_s: case ILOpCode.Bgt_s:
        case ILOpCode.Ble_s: case ILOpCode.Blt_s: case ILOpCode.Bne_un_s:
        case ILOpCode.Bge_un_s: case ILOpCode.Bgt_un_s: case ILOpCode.Ble_un_s:
        case ILOpCode.Blt_un_s: case ILOpCode.Leave_s:
            reader.ReadByte();
            break;

        // 2-byte operand (InlineVar).
        case ILOpCode.Ldarg: case ILOpCode.Ldarga: case ILOpCode.Starg:
        case ILOpCode.Ldloc: case ILOpCode.Ldloca: case ILOpCode.Stloc:
            reader.ReadInt16();
            break;

        // 4-byte operand (InlineI / ShortInlineR / InlineBrTarget / token kinds).
        case ILOpCode.Ldc_i4: case ILOpCode.Ldc_r4:
        case ILOpCode.Br: case ILOpCode.Brfalse: case ILOpCode.Brtrue:
        case ILOpCode.Beq: case ILOpCode.Bge: case ILOpCode.Bgt:
        case ILOpCode.Ble: case ILOpCode.Blt: case ILOpCode.Bne_un:
        case ILOpCode.Bge_un: case ILOpCode.Bgt_un: case ILOpCode.Ble_un:
        case ILOpCode.Blt_un: case ILOpCode.Leave:
        case ILOpCode.Call: case ILOpCode.Calli: case ILOpCode.Callvirt:
        case ILOpCode.Jmp: case ILOpCode.Newobj: case ILOpCode.Ldftn:
        case ILOpCode.Ldvirtftn:
        case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld:
        case ILOpCode.Ldsfld: case ILOpCode.Ldsflda: case ILOpCode.Stsfld:
        case ILOpCode.Cpobj: case ILOpCode.Ldobj: case ILOpCode.Castclass:
        case ILOpCode.Isinst: case ILOpCode.Unbox: case ILOpCode.Unbox_any:
        case ILOpCode.Stobj: case ILOpCode.Box: case ILOpCode.Newarr:
        case ILOpCode.Ldelema: case ILOpCode.Ldelem: case ILOpCode.Stelem:
        case ILOpCode.Refanyval: case ILOpCode.Mkrefany: case ILOpCode.Initobj:
        case ILOpCode.Constrained: case ILOpCode.Sizeof: case ILOpCode.Ldtoken:
        case ILOpCode.Ldstr:
            reader.ReadInt32();
            break;

        // 8-byte operand (InlineI8 / InlineR).
        case ILOpCode.Ldc_i8: case ILOpCode.Ldc_r8:
            reader.ReadInt64();
            break;

        // InlineSwitch: 4-byte count followed by count 4-byte targets.
        case ILOpCode.Switch:
            var n = reader.ReadUInt32();
            for (var i = 0; i < n; i++) reader.ReadInt32();
            break;
    }
}
