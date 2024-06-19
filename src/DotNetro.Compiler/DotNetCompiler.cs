using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using DotNetro.Compiler.CodeGen;
using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

public sealed class DotNetCompiler : IDisposable
{
    public static void Compile(string dotNetAssemblyPath, string entryPointMethodName, string outputPath, ILogger? logger)
    {
        var assemblyCode = Compile(dotNetAssemblyPath, entryPointMethodName, logger);

        File.WriteAllText(outputPath, assemblyCode);
    }

    public static string Compile(string dotNetAssemblyPath, string entryPointMethodName, ILogger? logger)
    {
        using var outputWriter = new StringWriter();

        using var compiler = new DotNetCompiler(outputWriter, dotNetAssemblyPath);

        compiler.Compile(entryPointMethodName);

        return outputWriter.ToString();
    }

    private readonly CodeGenerator _codeGenerator;
    private readonly TextWriter _output;

    private readonly TypeSystem.TypeSystem _typeSystem;
    private readonly AssemblyStore _assemblyStore;
    private readonly EcmaAssembly _rootAssemblyContext;

    private readonly Dictionary<string, string> _stringTable = [];
    private readonly HashSet<EcmaField> _staticFields = [];

    private readonly HashSet<EcmaMethod> _visitedMethods = [];
    private readonly Queue<EcmaMethod> _methodsToVisit = new();

    private readonly Stack<TypeDescription> _stack = new();

    private DotNetCompiler(TextWriter output, string rootAssemblyPath)
    {
        _codeGenerator = new BbcMicroCodeGenerator(output);

        _typeSystem = new TypeSystem.TypeSystem(_codeGenerator.PointerSize);
        _assemblyStore = new AssemblyStore(_typeSystem);

        _rootAssemblyContext = _assemblyStore.GetAssembly(rootAssemblyPath);

        _output = output;
    }

    public void Dispose()
    {
        _assemblyStore.Dispose();
    }

    private void Compile(string entryPointMethodName)
    {
        EcmaMethod? entryPointMethod = null;
        foreach (var methodDefinitionHandle in _rootAssemblyContext.MetadataReader.MethodDefinitions)
        {
            var methodDefinition = _rootAssemblyContext.MetadataReader.GetMethodDefinition(methodDefinitionHandle);

            var methodDefinitionName = _rootAssemblyContext.MetadataReader.GetString(methodDefinition.Name);

            if (methodDefinitionName == entryPointMethodName)
            {
                entryPointMethod = _rootAssemblyContext.GetMethod(methodDefinitionHandle);
                EnqueueMethod(entryPointMethod);
                break;
            }
        }

        if (entryPointMethod == null)
        {
            throw new InvalidOperationException($"Could not find entry point method {entryPointMethodName}");
        }

        _codeGenerator.WriteHeader();
        _codeGenerator.WriteEntryPoint(entryPointMethod.UniqueName);

        while (_methodsToVisit.Count > 0)
        {
            CompileMethod(_methodsToVisit.Dequeue());
        }

        foreach (var kvp in _stringTable)
        {
            _codeGenerator.WriteStringConstant(kvp.Key, kvp.Value);
        }

        foreach (var field in _staticFields)
        {
            _codeGenerator.WriteStaticField(field);
        }

        _codeGenerator.WriteFooter();
    }

    private void EnqueueMethod(EcmaMethod methodContext)
    {
        if (_visitedMethods.Add(methodContext))
        {
            _methodsToVisit.Enqueue(methodContext);
        }
    }

    private void CompileMethod(EcmaMethod methodContext)
    {
        _codeGenerator.WriteMethodStart(methodContext.UniqueName);

        // Is this a specially-handled method?
        switch (methodContext.UniqueName)
        {
            case "System_Console_Beep":
                _codeGenerator.CompileSystemConsoleBeep();
                break;

            case "System_Console_ReadLine":
                _codeGenerator.CompileSystemConsoleReadLine();
                break;

            case "System_Console_WriteLine_Int32":
                _codeGenerator.CompileSystemConsoleWriteLineInt32();
                break;

            case "System_Console_WriteLine_String":
                _codeGenerator.CompileSystemConsoleWriteLineString();
                break;

            default:
                CompileMethodBody(methodContext);
                break;
        }

        _codeGenerator.WriteMethodEnd();
    }

    private void CompileMethodBody(EcmaMethod methodContext)
    {
        var ilReader = methodContext.MethodBody.GetILReader();

        while (ilReader.RemainingBytes > 0)
        {
            _codeGenerator.WriteLabel($"IL_{ilReader.Offset:x4}");

            var opCode = ReadOpCode(ref ilReader);

            switch (opCode)
            {
                case ILOpCode.Add:
                    CompileAdd();
                    break;

                case ILOpCode.Blt_s:
                    CompileBlt(methodContext, "blt.s", ilReader.ReadSByte() + ilReader.Offset);
                    break;

                case ILOpCode.Br_s:
                    CompileBr(ilReader.ReadSByte() + ilReader.Offset);
                    break;

                case ILOpCode.Brtrue_s:
                    CompileBrtrue(ilReader.ReadSByte() + ilReader.Offset);
                    break;

                case ILOpCode.Call:
                    CompileCall(methodContext, MetadataTokens.Handle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Clt:
                    CompileClt();
                    break;

                case ILOpCode.Dup:
                    CompileDup();
                    break;

                case ILOpCode.Initobj:
                    CompileInitobj(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Ldarg_0:
                    CompileLdarg(methodContext, "ldarg.0", 0);
                    break;

                case ILOpCode.Ldarg_1:
                    CompileLdarg(methodContext, "ldarg.1", 1);
                    break;

                case ILOpCode.Ldarg_2:
                    CompileLdarg(methodContext, "ldarg.2", 2);
                    break;

                case ILOpCode.Ldarg_3:
                    CompileLdarg(methodContext, "ldarg.3", 3);
                    break;

                case ILOpCode.Ldc_i4_0:
                    CompileLdcI4($"ldc.i4.0", 0);
                    break;

                case ILOpCode.Ldc_i4_1:
                    CompileLdcI4($"ldc.i4.1", 1);
                    break;

                case ILOpCode.Ldc_i4_2:
                    CompileLdcI4($"ldc.i4.2", 2);
                    break;

                case ILOpCode.Ldc_i4_3:
                    CompileLdcI4($"ldc.i4.3", 3);
                    break;

                case ILOpCode.Ldc_i4_4:
                    CompileLdcI4($"ldc.i4.4", 4);
                    break;

                case ILOpCode.Ldc_i4_5:
                    CompileLdcI4($"ldc.i4.5", 5);
                    break;

                case ILOpCode.Ldc_i4_m1:
                    CompileLdcI4($"ldc.i4.m1", -1);
                    break;

                case ILOpCode.Ldc_i4_s:
                    var value = ilReader.ReadSByte();
                    CompileLdcI4($"ldc.i4.s {value}", value);
                    break;

                case ILOpCode.Ldfld:
                    CompileLdfld(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Ldloc_0:
                    CompileLdloc(methodContext, 0);
                    break;

                case ILOpCode.Ldloc_1:
                    CompileLdloc(methodContext, 1);
                    break;

                case ILOpCode.Ldloc_2:
                    CompileLdloc(methodContext, 2);
                    break;

                case ILOpCode.Ldloca_s:
                    CompileLdloca(methodContext, ilReader.ReadByte());
                    break;

                case ILOpCode.Ldsfld:
                    CompileLdsfld(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Ldstr:
                    CompileLdstr(methodContext, ilReader.ReadInt32());
                    break;

                case ILOpCode.Nop:
                    CompileNop();
                    break;

                case ILOpCode.Ret:
                    CompileRet(methodContext);
                    break;

                case ILOpCode.Stfld:
                    CompileStfld(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Stloc_0:
                    CompileStloc(methodContext, 0);
                    break;

                case ILOpCode.Stloc_1:
                    CompileStloc(methodContext, 1);
                    break;

                case ILOpCode.Stsfld:
                    CompileStsfld(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                default:
                    throw new NotImplementedException($"OpCode {opCode} not implemented");
            }

            _output.WriteLine();
        }
    }

    private void CompileAdd()
    {
        var rightType = PopStackEntry();
        var leftType = PopStackEntry();

        if (leftType != rightType)
        {
            throw new NotSupportedException();
        }

        if (leftType is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Int32 })
        {
            throw new NotSupportedException();
        }

        PushStackEntry(leftType);

        _codeGenerator.WriteComment("add");
        _codeGenerator.WriteAddInt32();
    }

    private void CompileBlt(EcmaMethod methodContext, string mnemonic, int target)
    {
        // TODO: Optimize this.

        CompileClt();
        CompileBrtrue(target);
    }

    private void CompileBr(int target)
    {
        var label = $"IL_{target:x4}";

        _codeGenerator.WriteComment($"br.s {label}");
        _codeGenerator.WriteBr(label);
    }

    private void CompileBrtrue(int target)
    {
        var stackObjectType = PopStackEntry();

        var label = $"IL_{target:x4}";

        _codeGenerator.WriteComment($"brtrue.s {label}");
        _codeGenerator.WriteBrtrue(stackObjectType, label);
    }

    private void CompileCall(EcmaMethod methodContext, Handle methodHandle)
    {
        var callee = methodContext.DeclaringType.Assembly.ResolveMethod(methodHandle);

        for (var i = 0; i < callee.MethodSignature.ParameterTypes.Length; i++)
        {
            if (PopStackEntry() != callee.MethodSignature.ParameterTypes[i])
            {
                throw new InvalidOperationException();
            }
        }

        if (callee.MethodSignature.ReturnType is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Void })
        {
            PushStackEntry(callee.MethodSignature.ReturnType);
        }

        EnqueueMethod(callee);

        _codeGenerator.WriteComment($"call {callee.UniqueName}");
        _codeGenerator.WriteCall(methodContext, callee);        
    }

    private void CompileClt()
    {
        var rightType = PopStackEntry();
        var leftType = PopStackEntry();

        if (leftType != rightType)
        {
            throw new NotSupportedException();
        }

        if (leftType is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Int32 })
        {
            throw new NotSupportedException();
        }

        PushStackEntry(_typeSystem.Int32);

        _codeGenerator.WriteComment("clt");
        _codeGenerator.WriteCltInt32();
    }

    private void CompileDup()
    {
        var type = PopStackEntry();

        PushStackEntry(type);
        PushStackEntry(type);

        _codeGenerator.WriteComment("dup");
        _codeGenerator.WriteDup(type);
    }

    private void CompileInitobj(EcmaMethod methodContext, EntityHandle typeHandle)
    {
        var type = methodContext.DeclaringType.Assembly.ResolveType(typeHandle);

        if (PopStackEntry() is not PointerType)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment($"initobj {type.FullName}");
        _codeGenerator.WriteInitobj(type);
    }

    private void CompileLdarg(EcmaMethod methodContext, string mnemonic, int index)
    {
        var parameter = methodContext.Parameters[index];

        PushStackEntry(parameter.Type);

        _codeGenerator.WriteComment(mnemonic);
        _codeGenerator.WriteLdarg(parameter);
    }

    private void CompileLdcI4(string mnemonic, int value)
    {
        PushStackEntry(_typeSystem.Int32);

        _codeGenerator.WriteComment(mnemonic);
        _codeGenerator.WriteLdcI4(value);
    }

    private void CompileLdfld(EcmaMethod methodContext, EntityHandle fieldHandle)
    {
        var field = methodContext.DeclaringType.Assembly.GetField((FieldDefinitionHandle)fieldHandle);

        var objectType = PopStackEntry();

        PushStackEntry(field.Type);

        _codeGenerator.WriteComment($"ldfld {field.Owner.FullName}::{field.Name}");
        _codeGenerator.WriteLdfld(objectType, field);
    }

    private void CompileLdloc(EcmaMethod methodContext, int index)
    {
        var local = methodContext.LocalVariables[index];

        PushStackEntry(local.Type);

        _codeGenerator.WriteComment($"ldloc.{local.Index}");
        _codeGenerator.WriteLdloc(local);
    }

    private void CompileLdloca(EcmaMethod methodContext, int index)
    {
        var local = methodContext.LocalVariables[index];

        PushStackEntry(_typeSystem.GetPointerType(local.Type));

        _codeGenerator.WriteComment($"ldloca.s {local.Index}");
        _codeGenerator.WriteLdloca(local);
    }

    private void CompileLdsfld(EcmaMethod methodContext, EntityHandle fieldHandle)
    {
        var field = methodContext.DeclaringType.Assembly.GetField((FieldDefinitionHandle)fieldHandle);

        EnsureStaticField(field);

        PushStackEntry(field.Type);

        _codeGenerator.WriteComment($"ldsfld {field.Owner.FullName}::{field.Name}");
        _codeGenerator.WriteLdsfld(field);
    }

    private void CompileLdstr(EcmaMethod methodContext, int token)
    {
        var stringValue = methodContext.MetadataReader.GetUserString(MetadataTokens.UserStringHandle(token));

        PushStackEntry(_typeSystem.String);

        var stringKey = $"string{token:X8}";
        _stringTable[stringKey] = stringValue;

        _codeGenerator.WriteComment($"ldstr \"{stringValue}\"");
        _codeGenerator.WriteLdstr(stringKey);
    }

    private void CompileNop()
    {
        _output.WriteLine("    ; nop");
    }

    private void CompileRet(EcmaMethod methodContext)
    {
        if (methodContext.MethodSignature.ReturnType is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Void })
        {
            if (PopStackEntry() != methodContext.MethodSignature.ReturnType)
            {
                throw new InvalidOperationException();
            }
        }

        if (_stack.Count > 0)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment("ret");
        _codeGenerator.WriteRet();
    }

    private void CompileStfld(EcmaMethod methodContext, EntityHandle fieldHandle)
    {
        var field = methodContext.DeclaringType.Assembly.GetField((FieldDefinitionHandle)fieldHandle);

        var valueType = PopStackEntry();

        if (valueType != field.Type)
        {
            throw new InvalidOperationException();
        }

        var objectReferenceType = PopStackEntry();
        if (objectReferenceType is not PointerType)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment($"stfld {field.Owner.FullName}::{field.Name}");
        _codeGenerator.WriteStfld(field);
    }

    private void CompileStloc(EcmaMethod methodContext, int index)
    {
        var local = methodContext.LocalVariables[index];
        var stackEntryType = PopStackEntry();

        _codeGenerator.WriteComment($"stloc.{local.Index}");
        _codeGenerator.WriteStloc(stackEntryType, local);
    }

    private void CompileStsfld(EcmaMethod methodContext, EntityHandle fieldHandle)
    {
        var field = methodContext.DeclaringType.Assembly.GetField((FieldDefinitionHandle)fieldHandle);

        EnsureStaticField(field);

        var valueType = PopStackEntry();

        if (valueType != field.Type)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment($"stsfld {field.Owner.FullName}::{field.Name}");
        _codeGenerator.WriteStsfld(field);
    }

    private void EnsureStaticField(EcmaField field)
    {
        _staticFields.Add(field);
    }

    private void PushStackEntry(TypeDescription type) => _stack.Push(type);

    private TypeDescription PopStackEntry() => _stack.Pop();

    private static ILOpCode ReadOpCode(ref BlobReader ilReader)
    {
        var opCodeByte = ilReader.ReadByte();

        return (ILOpCode)(opCodeByte == 0xFE 
            ? 0xFE00 + ilReader.ReadByte()
            : opCodeByte);
    }
}