using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using DotNetro.Compiler.CodeGen;
using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

public sealed class DotNetCompiler : IDisposable
{
    public static string Compile(string dotNetAssemblyPath, string entryPointMethodName, ILogger? logger)
    {
        using var outputWriter = new StringWriter();

        using var compiler = new DotNetCompiler(outputWriter, dotNetAssemblyPath);

        compiler.Compile(entryPointMethodName);

        return outputWriter.ToString();
    }

    private readonly CodeGenerator _codeGenerator;
    private readonly TextWriter _output;

    private readonly TypeSystemContext _typeSystemContext;
    private readonly EcmaAssembly _rootAssemblyContext;

    private readonly Dictionary<string, string> _stringTable = [];
    private readonly HashSet<EcmaField> _staticFields = [];

    private readonly HashSet<EcmaMethod> _visitedMethods = [];
    private readonly Queue<EcmaMethod> _methodsToVisit = new();

    private readonly Stack<TypeDescription> _stack = new();

    private readonly VtableTracker _vtableTracker = new();

    private DotNetCompiler(TextWriter output, string rootAssemblyPath)
    {
        _codeGenerator = new BbcMicroCodeGenerator(output);

        _typeSystemContext = new TypeSystemContext(_codeGenerator.PointerSize);

        _rootAssemblyContext = _typeSystemContext.ResolveAssembly(rootAssemblyPath);

        _output = output;
    }

    public void Dispose()
    {
        _typeSystemContext.Dispose();
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

        var staticConstructors = _staticFields
            .Select(x => x.Owner.GetStaticConstructor())
            .Where(x => x != null)
            .Select(x => x!)
            .Distinct()
            .ToArray();

        foreach (var staticConstructor in staticConstructors)
        {
            CompileMethod(staticConstructor);
        }

        foreach (var field in _staticFields)
        {
            _codeGenerator.WriteStaticField(field);
        }

        foreach (var kvp in _stringTable)
        {
            _codeGenerator.WriteStringConstant(kvp.Key, kvp.Value);
        }

        _codeGenerator.WriteVtables(_vtableTracker.BuildVtables());

        _codeGenerator.WriteFooter(staticConstructors);
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

        // Queue any new virtual methods.
        // TODO: Don't do it like this.
        foreach (var vtable in _vtableTracker.BuildVtables())
        {
            foreach (var slot in vtable.Slots)
            {
                EnqueueMethod(slot.Method);
            }
        }

        _codeGenerator.WriteMethodEnd();
    }

    private void CompileMethodBody(EcmaMethod methodContext)
    {
        var ilReader = methodContext.MethodBody!.MethodBodyBlock.GetILReader();

        while (ilReader.RemainingBytes > 0)
        {
            _codeGenerator.WriteLabel(GetLabel(ilReader.Offset));

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
                    CompileCall(methodContext, MetadataTokens.Handle(ilReader.ReadInt32()), CallType.Normal);
                    break;

                case ILOpCode.Callvirt:
                    CompileCall(methodContext, MetadataTokens.Handle(ilReader.ReadInt32()), CallType.Virtual);
                    break;

                case ILOpCode.Clt:
                    CompileClt();
                    break;

                case ILOpCode.Conv_i:
                    CompileConvi();
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
                {
                    var value = ilReader.ReadSByte();
                    CompileLdcI4($"ldc.i4.s {value}", value);
                    break;
                }

                case ILOpCode.Ldc_i4:
                {
                    var value = ilReader.ReadInt32();
                    CompileLdcI4($"ldc.i4 {value}", value);
                    break;
                }

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

                case ILOpCode.Newobj:
                    CompileNewobj(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Nop:
                    CompileNop();
                    break;

                case ILOpCode.Ret:
                    CompileRet(methodContext);
                    break;

                case ILOpCode.Sizeof:
                    CompileSizeof(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Stfld:
                    CompileStfld(methodContext, MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                    break;

                case ILOpCode.Stind_i:
                    CompileStind(_typeSystemContext.IntPtr);
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

        PushStackEntry(leftType);

        _codeGenerator.WriteComment("add");

        switch (leftType)
        {
            case PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Int32 }:
                _codeGenerator.WriteAddInt32();
                break;

            case PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.IntPtr }:
                _codeGenerator.WriteAddIntPtr();
                break;

            default:
                throw new NotSupportedException();
        }
    }

    private void CompileBlt(EcmaMethod methodContext, string mnemonic, int target)
    {
        // TODO: Optimize this.

        CompileClt();
        CompileBrtrue(target);
    }

    private void CompileBr(int target)
    {
        var label = GetLabel(target);

        _codeGenerator.WriteComment($"br.s {label}");
        _codeGenerator.WriteBr(label);
    }

    private void CompileBrtrue(int target)
    {
        var stackObjectType = PopStackEntry();

        var label = GetLabel(target);

        _codeGenerator.WriteComment($"brtrue.s {label}");
        _codeGenerator.WriteBrtrue(stackObjectType, label);
    }

    private enum CallType
    {
        Normal,
        Virtual,
    }

    private void CompileCall(EcmaMethod methodContext, Handle methodHandle, CallType callType)
    {
        var callee = methodContext.DeclaringType.Assembly.ResolveMethod(methodHandle);

        for (var i = callee.Parameters.Length - 1; i >= 0; i--)
        {
            var actualParameterType = PopStackEntry();
            if (actualParameterType != callee.Parameters[i].Type)
            {
                // TODO: Need to check for implicit conversions.
                //throw new InvalidOperationException();
            }
        }

        if (callee.MethodSignature.ReturnType is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Void })
        {
            PushStackEntry(callee.MethodSignature.ReturnType);
        }

        EnqueueMethod(callee);

        if (callType == CallType.Virtual && callee.IsVirtual)
        {
            var vtableSlotLabel = _vtableTracker.GetVtableSlotLabel(callee);

            _codeGenerator.WriteComment($"callvirt {callee.UniqueName}");
            _codeGenerator.WriteCallvirt(methodContext, callee, vtableSlotLabel);
        }
        else
        {
            _codeGenerator.WriteComment($"call {callee.UniqueName}");
            _codeGenerator.WriteCall(methodContext, callee);
        }
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

        PushStackEntry(_typeSystemContext.Boolean);

        _codeGenerator.WriteComment("clt");
        _codeGenerator.WriteCltInt32();
    }

    private void CompileConvi()
    {
        var type = PopStackEntry();

        if (type is not PrimitiveType { PrimitiveTypeCode: PrimitiveTypeCode.Int32 })
        {
            throw new NotSupportedException();
        }

        PushStackEntry(_typeSystemContext.IntPtr);

        _codeGenerator.WriteComment("conv.i");
        _codeGenerator.WriteConviInt32();
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

        if (PopStackEntry() is not ByReferenceType)
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
        PushStackEntry(_typeSystemContext.Int32);

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
        var local = methodContext.MethodBody!.LocalVariables[index];

        PushStackEntry(local.Type);

        _codeGenerator.WriteComment($"ldloc.{local.Index}");
        _codeGenerator.WriteLdloc(local);
    }

    private void CompileLdloca(EcmaMethod methodContext, int index)
    {
        var local = methodContext.MethodBody!.LocalVariables[index];

        PushStackEntry(local.Type.MakeByReferenceType());

        _codeGenerator.WriteComment($"ldloca {local.Index}");
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

        PushStackEntry(_typeSystemContext.String);

        var stringKey = $"string{token:X8}";
        _stringTable[stringKey] = stringValue;

        _codeGenerator.WriteComment($"ldstr \"{stringValue}\"");
        _codeGenerator.WriteLdstr(stringKey);
    }

    private void CompileNewobj(EcmaMethod methodContext, EntityHandle methodHandle)
    {
        var constructor = methodContext.DeclaringType.Assembly.ResolveMethod(methodHandle);

        EnqueueMethod(constructor);

        // Skip the first parameter, which is the `this` parameter.
        for (var i = constructor.Parameters.Length - 1; i >= 1; i--)
        {
            if (PopStackEntry() != constructor.Parameters[i].Type)
            {
                throw new InvalidOperationException();
            }
        }

        PushStackEntry(constructor.DeclaringType);

        _vtableTracker.MarkTypeUsed(constructor.DeclaringType);

        // Resolve ManagedHeap.Alloc method.
        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        var allocMethod = managedHeapType.GetMethod("Alloc", new MethodSignature<TypeDescription>(new SignatureHeader(), _typeSystemContext.IntPtr, 2, 0, [_typeSystemContext.IntPtr, _typeSystemContext.IntPtr]));
        EnqueueMethod(allocMethod);

        _codeGenerator.WriteComment($"newobj {constructor.UniqueName}");
        _codeGenerator.WriteNewobj(methodContext, constructor, allocMethod);
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

    private void CompileSizeof(EcmaMethod methodContext, EntityHandle typeHandle)
    {
        var type = methodContext.DeclaringType.Assembly.ResolveType(typeHandle);

        PushStackEntry(_typeSystemContext.Int32);

        _codeGenerator.WriteComment($"sizeof {type.FullName}");
        _codeGenerator.WriteLdcI4(type.Size);
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
        if (!objectReferenceType.IsPointerLike)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment($"stfld {field.Owner.FullName}::{field.Name}");
        _codeGenerator.WriteStfld(field);
    }

    private void CompileStind(TypeDescription type)
    {
        var valueType = PopStackEntry();
        var pointerType = PopStackEntry();

        if (!pointerType.IsPointerLike)
        {
            throw new InvalidOperationException();
        }

        if (valueType != type)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment("stind");
        _codeGenerator.WriteStind(type);
    }

    private void CompileStloc(EcmaMethod methodContext, int index)
    {
        var local = methodContext.MethodBody!.LocalVariables[index];
        var stackEntryType = PopStackEntry();

        if (stackEntryType != local.Type)
        {
            throw new InvalidOperationException();
        }

        _codeGenerator.WriteComment($"stloc.{local.Index}");
        _codeGenerator.WriteStloc(local);
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

    private static string GetLabel(int target) => $"IL_{target:x4}";
}