using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using System.Text;

using DotNetro.Compiler.TypeSystem;

using Irie.Dialects.Arith;
using Irie.Dialects.Core;
using Irie.Dialects.Mem;
using Irie.Mir;

namespace DotNetro.Compiler;

// Walks the same IL as DotNetCompiler but, instead of emitting 6502 assembly
// text, builds an Irie MirModule via MirBuilder. The module is then handed to
// the Irie MOS6502 pass pipeline (see CompilerDriver) to produce program bytes.
//
// This is the step-7/8 skeleton: only the IL ops required for Print42 and
// AddInt32 are implemented. Unimplemented ops throw NotSupportedException so the
// gaps are visible as the translator grows (plan step 9).
internal sealed class IlToMirTranslator : IDisposable
{
    private readonly TypeSystemContext _typeSystemContext;
    private readonly EcmaAssembly _rootAssemblyContext;

    private readonly HashSet<EcmaMethod> _visitedMethods = [];
    private readonly Queue<EcmaMethod> _methodsToVisit = new();

    // Names of functions supplied by the hand-written runtime; the BFS walk must
    // not try to translate (or recurse into) these — they already exist as MIR.
    private readonly HashSet<string> _runtimeFunctionNames = [];

    private readonly string _runtimeText;

    // Module-level globals (string constants, static fields) collected during
    // the IL walk and appended to the module once translation completes. Keyed
    // by symbol name so the same string / static field is registered only once.
    private readonly Dictionary<string, MirGlobal> _globals = [];

    // Pointer size is 16-bit on the 6502.
    private const int PointerSize = 2;

    public IlToMirTranslator(string rootAssemblyPath, string runtimeText)
    {
        _runtimeText = runtimeText;
        _typeSystemContext = new TypeSystemContext(PointerSize);
        _rootAssemblyContext = _typeSystemContext.ResolveAssembly(rootAssemblyPath);
    }

    public void Dispose() => _typeSystemContext.Dispose();

    public MirModule Translate(string entryPointMethodName)
    {
        var entryPointMethod = FindMethod(entryPointMethodName)
            ?? FindMethod("<Main>$")
            ?? throw new InvalidOperationException(
                $"Could not find entry point method {entryPointMethodName}");

        // Parse the hand-written runtime first so its functions are available
        // (and so we know which names to skip during the IL walk).
        using var runtimeReader = new StringReader(_runtimeText);
        var runtimeModule = MirModule.Parse(runtimeReader);
        foreach (var function in runtimeModule.Functions)
            _runtimeFunctionNames.Add(function.Name);

        var module = new MirModule();

        // The entry function must lay out first (at `origin`) so the emulator's
        // `JSR $2000` lands on it. It performs BBC MODE 7 init, calls Main, RTS.
        module.Functions.Add(BuildEntryFunction(entryPointMethod));

        EnqueueMethod(entryPointMethod);
        while (_methodsToVisit.Count > 0)
        {
            var method = _methodsToVisit.Dequeue();
            module.Functions.Add(TranslateMethod(method));
        }

        // Append the runtime functions (oswrch, osasci, WriteLineInt32, …).
        module.Functions.AddRange(runtimeModule.Functions);

        // Append the string constants / static fields collected during the walk.
        module.Globals.AddRange(_globals.Values);

        return module;
    }

    // Intern a string constant as a module-level MirGlobal and return its
    // symbol name. Mirrors DotNetCompiler.CompileLdstr: the key is
    // `string{token:X8}` and the payload is the ASCII bytes followed by CR
    // (0x0D) then NUL (0x00) — i.e. the legacy `.cstring "...", 13` format.
    // @WriteLineString loops over the bytes calling osasci (which expands CR to
    // CR/LF) until it reaches the NUL terminator.
    private string RegisterStringGlobal(string stringKey, string value)
    {
        if (!_globals.ContainsKey(stringKey))
        {
            var ascii = Encoding.ASCII.GetBytes(value);
            var bytes = new byte[ascii.Length + 2];
            Array.Copy(ascii, bytes, ascii.Length);
            bytes[ascii.Length] = 0x0D;     // CR, matching `.cstring "...", 13`
            bytes[ascii.Length + 1] = 0x00; // NUL terminator

            _globals[stringKey] = new MirGlobal(
                SymbolName:  stringKey,
                Type:        IRType.Pointer,
                SizeInBytes: bytes.Length,
                Initializer: new MirInitializer([new DataBytes(bytes)]));
        }

        return stringKey;
    }

    // Register a static field as a zero-initialized (.bss-style) MirGlobal and
    // return its symbol name. Mirrors DotNetCompiler's GetStaticFieldName:
    // `{field.Owner.EncodedName}_{field.Name}`.
    private string RegisterStaticFieldGlobal(EcmaField field)
    {
        var name = $"{field.Owner.EncodedName}_{field.Name}";
        if (!_globals.ContainsKey(name))
        {
            _globals[name] = new MirGlobal(
                SymbolName:  name,
                Type:        ToIRType(field.Type),
                SizeInBytes: field.Type.Size,
                Initializer: null);
        }

        return name;
    }

    private EcmaMethod? FindMethod(string methodName)
    {
        foreach (var methodDefinitionHandle in _rootAssemblyContext.MetadataReader.MethodDefinitions)
        {
            var methodDefinition = _rootAssemblyContext.MetadataReader.GetMethodDefinition(methodDefinitionHandle);
            var name = _rootAssemblyContext.MetadataReader.GetString(methodDefinition.Name);
            if (name == methodName)
                return _rootAssemblyContext.GetMethod(methodDefinitionHandle);
        }
        return null;
    }

    private void EnqueueMethod(EcmaMethod method)
    {
        if (_runtimeFunctionNames.Contains(method.UniqueName))
            return;
        if (_visitedMethods.Add(method))
            _methodsToVisit.Enqueue(method);
    }

    // Synthesize the program entry point:
    //   func @__entry : () -> void {
    //     call.func @oswrch(22)   ; MODE 7
    //     call.func @oswrch(7)    ; MODE 7
    //     call.func @<Main>()
    //     core.return
    //   }
    private MirFunction BuildEntryFunction(EcmaMethod entryPointMethod)
    {
        var function = new MirFunction("__entry", [], IRType.Void);
        var entryBlock = function.CreateBlock();
        var builder = new MirBuilder(function);
        builder.SetInsertionPointAtEnd(entryBlock);

        EmitModeSeven(function, builder);

        // Top-level statements compile to `<Main>$(string[] args)`. The args
        // array is unused by the programs we support, but the call's arity must
        // still match the callee, so pass a null (constant-0) pointer per param.
        var argVregs = new int[entryPointMethod.Parameters.Length];
        for (var i = 0; i < entryPointMethod.Parameters.Length; i++)
        {
            var type = ToIRType(entryPointMethod.Parameters[i].Type);
            argVregs[i] = builder.BuildConstant(type, 0);
        }

        builder.BuildCall(entryPointMethod.UniqueName, [], argVregs);

        builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return));
        return function;
    }

    private static void EmitModeSeven(MirFunction function, MirBuilder builder)
    {
        // VDU 22, 7 selects MODE 7 on the BBC Micro. oswrch takes a single i8.
        EmitOswrch(function, builder, 22);
        EmitOswrch(function, builder, 7);
    }

    private static void EmitOswrch(MirFunction function, MirBuilder builder, long value)
    {
        var c = builder.BuildConstant(IRType.I8, value);
        builder.BuildCall("oswrch", [], c);
    }

    private MirFunction TranslateMethod(EcmaMethod method)
    {
        var paramTypes = method.Parameters
            .Select(p => ToIRType(p.Type))
            .ToArray();
        var returnType = ToIRType(method.MethodSignature.ReturnType);

        var function = new MirFunction(method.UniqueName, paramTypes, returnType);
        var methodTranslator = new MethodTranslator(this, method, function);
        methodTranslator.Translate();
        return function;
    }

    private static IRType ToIRType(TypeDescription type)
    {
        if (type is PrimitiveType primitive)
        {
            return primitive.PrimitiveTypeCode switch
            {
                PrimitiveTypeCode.Void => IRType.Void,
                PrimitiveTypeCode.Boolean => IRType.I8,
                PrimitiveTypeCode.Int32 => IRType.I32,
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => IRType.I16,
                _ => throw new NotSupportedException(
                    $"IL→MIR: primitive type {primitive.PrimitiveTypeCode} is not supported yet."),
            };
        }

        // Reference types (including arrays) are heap pointers: 16-bit on the
        // 6502. Fuller object-model support lands in plan step 9; for now this
        // only matters for the unused `string[] args` of the entry method.
        if (type is SZArrayType or PointerType or ByReferenceType)
            return IRType.I16;

        throw new NotSupportedException(
            $"IL→MIR: type {type} is not supported yet.");
    }

    // Translates a single method body. One instance per method so the eval stack
    // and local SSA-name maps are per-method.
    private sealed class MethodTranslator
    {
        private readonly IlToMirTranslator _parent;
        private readonly EcmaMethod _method;
        private readonly MirFunction _function;
        private readonly MirBuilder _builder;

        // Mirrors the IL evaluation stack: vreg IDs (paired with their type so
        // we can size arith ops). Runs alongside the IL semantics.
        private readonly Stack<StackValue> _stack = new();

        // Primitive SSA locals: local index → current vreg ID (and its type).
        private readonly Dictionary<int, StackValue> _locals = [];

        public MethodTranslator(IlToMirTranslator parent, EcmaMethod method, MirFunction function)
        {
            _parent = parent;
            _method = method;
            _function = function;
            _builder = new MirBuilder(function);
        }

        public void Translate()
        {
            var entryBlock = _function.CreateBlock();

            // Parameters become entry-block parameter vregs (lowered by
            // AbiLoweringPass). Record them so ldarg.N can read them.
            var paramVregs = new int[_method.Parameters.Length];
            for (var i = 0; i < _method.Parameters.Length; i++)
            {
                var type = ToIRType(_method.Parameters[i].Type);
                var vreg = _function.CreateVirtualRegister(type);
                entryBlock.Parameters.Add(vreg);
                paramVregs[i] = vreg;
            }
            _parameterVregs = paramVregs;

            _builder.SetInsertionPointAtEnd(entryBlock);

            var ilReader = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (ilReader.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref ilReader);
                switch (opCode)
                {
                    case ILOpCode.Nop:
                        break;

                    case ILOpCode.Ldc_i4_0: PushConstant(0); break;
                    case ILOpCode.Ldc_i4_1: PushConstant(1); break;
                    case ILOpCode.Ldc_i4_2: PushConstant(2); break;
                    case ILOpCode.Ldc_i4_3: PushConstant(3); break;
                    case ILOpCode.Ldc_i4_4: PushConstant(4); break;
                    case ILOpCode.Ldc_i4_5: PushConstant(5); break;
                    case ILOpCode.Ldc_i4_m1: PushConstant(-1); break;
                    case ILOpCode.Ldc_i4_s: PushConstant(ilReader.ReadSByte()); break;
                    case ILOpCode.Ldc_i4: PushConstant(ilReader.ReadInt32()); break;

                    case ILOpCode.Add: TranslateAdd(); break;

                    case ILOpCode.Stloc_0: TranslateStloc(0); break;
                    case ILOpCode.Stloc_1: TranslateStloc(1); break;
                    case ILOpCode.Stloc_2: TranslateStloc(2); break;
                    case ILOpCode.Stloc_3: TranslateStloc(3); break;
                    case ILOpCode.Stloc_s: TranslateStloc(ilReader.ReadByte()); break;

                    case ILOpCode.Ldloc_0: TranslateLdloc(0); break;
                    case ILOpCode.Ldloc_1: TranslateLdloc(1); break;
                    case ILOpCode.Ldloc_2: TranslateLdloc(2); break;
                    case ILOpCode.Ldloc_3: TranslateLdloc(3); break;
                    case ILOpCode.Ldloc_s: TranslateLdloc(ilReader.ReadByte()); break;

                    case ILOpCode.Ldarg_0: TranslateLdarg(0); break;
                    case ILOpCode.Ldarg_1: TranslateLdarg(1); break;
                    case ILOpCode.Ldarg_2: TranslateLdarg(2); break;
                    case ILOpCode.Ldarg_3: TranslateLdarg(3); break;
                    case ILOpCode.Ldarg_s: TranslateLdarg(ilReader.ReadByte()); break;

                    case ILOpCode.Br_s:
                        TranslateBr(ilReader.ReadSByte() + ilReader.Offset, ilReader.Offset);
                        break;

                    case ILOpCode.Ldstr:
                        TranslateLdstr(ilReader.ReadInt32());
                        break;

                    case ILOpCode.Ldsfld:
                        TranslateLdsfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Stsfld:
                        TranslateStsfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Call:
                        TranslateCall(MetadataTokens.Handle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Ret:
                        TranslateRet();
                        break;

                    default:
                        throw new NotSupportedException(
                            $"IL→MIR: opcode {opCode} is not supported yet (method {_method.UniqueName}).");
                }
            }
        }

        private int[] _parameterVregs = [];

        private void PushConstant(int value)
        {
            // C# integer literals are i32 on the IL stack.
            var vreg = _builder.BuildConstant(IRType.I32, value);
            _stack.Push(new StackValue(vreg, IRType.I32));
        }

        private void TranslateAdd()
        {
            var right = _stack.Pop();
            var left = _stack.Pop();
            if (!left.Type.Equals(right.Type))
                throw new NotSupportedException(
                    $"IL→MIR: add of mismatched types {left.Type} and {right.Type}.");

            var result = _function.CreateVirtualRegister(left.Type);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(left.Vreg, IsDefinition: false),
                new VirtualReg(right.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, left.Type));
        }

        private void TranslateStloc(int index) => _locals[index] = _stack.Pop();

        private void TranslateLdloc(int index)
        {
            if (!_locals.TryGetValue(index, out var value))
                throw new NotSupportedException(
                    $"IL→MIR: ldloc of uninitialized local {index} (method {_method.UniqueName}).");
            _stack.Push(value);
        }

        private void TranslateLdarg(int index)
        {
            if (index >= _parameterVregs.Length)
                throw new NotSupportedException(
                    $"IL→MIR: ldarg {index} out of range (method {_method.UniqueName}).");
            var type = ToIRType(_method.Parameters[index].Type);
            _stack.Push(new StackValue(_parameterVregs[index], type));
        }

        // Roslyn's Debug-mode codegen routes every `return value;` through a
        // single epilogue: `stloc.0; br.s <end>; <end>: ldloc.0; ret`, where the
        // branch target is the instruction immediately following the branch. That
        // fall-through `br.s` is a no-op for our single-block model. General
        // (non-fall-through) control flow is a later porting group.
        private void TranslateBr(int target, int nextOffset)
        {
            if (target != nextOffset)
                throw new NotSupportedException(
                    $"IL→MIR: non-fall-through branch (br.s to {target}) is not supported yet " +
                    $"(method {_method.UniqueName}).");
        }

        // ldstr "..." → intern the string as a MirGlobal, push a `mem.symbol`
        // pointer (i16) to its bytes. WriteLineString consumes that pointer.
        private void TranslateLdstr(int token)
        {
            var value = _method.MetadataReader.GetUserString(
                MetadataTokens.UserStringHandle(token));
            var stringKey = $"string{token:X8}";
            _parent.RegisterStringGlobal(stringKey, value);

            var ptr = _builder.BuildMemSymbol(stringKey);
            _stack.Push(new StackValue(ptr, IRType.Pointer));
        }

        // ldsfld <field> → load the static field's value via its symbolic
        // address: %p = mem.symbol @<owner>_<field>; %v = mem.load.iN %p.
        private void TranslateLdsfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var name = _parent.RegisterStaticFieldGlobal(field);
            var type = ToIRType(field.Type);

            var ptr = _builder.BuildMemSymbol(name);
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(ptr, IsDefinition: false));
            _stack.Push(new StackValue(value, type));
        }

        // stsfld <field> → store the top of stack into the static field's
        // symbolic address: %p = mem.symbol @<owner>_<field>; mem.store.iN %p, %v.
        private void TranslateStsfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var name = _parent.RegisterStaticFieldGlobal(field);
            var type = ToIRType(field.Type);

            var value = _stack.Pop();
            var ptr = _builder.BuildMemSymbol(name);
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(ptr, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        private static MemOp LoadOpFor(IRType type) => type.SizeInBits switch
        {
            8 => MemOp.LoadI8,
            16 => MemOp.LoadI16,
            32 => MemOp.LoadI32,
            _ => throw new NotSupportedException(
                $"IL→MIR: mem.load of {type} is not supported yet."),
        };

        private static MemOp StoreOpFor(IRType type) => type.SizeInBits switch
        {
            8 => MemOp.StoreI8,
            16 => MemOp.StoreI16,
            32 => MemOp.StoreI32,
            _ => throw new NotSupportedException(
                $"IL→MIR: mem.store of {type} is not supported yet."),
        };

        private void TranslateCall(Handle methodHandle)
        {
            var callee = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);

            // Special-cased BCL methods map to hand-written runtime functions
            // (their real IL bodies are NOT translated/enqueued — the runtime
            // MIR replaces them).
            var calleeName = callee.UniqueName switch
            {
                "System_Console_WriteLine_Int32" => "WriteLineInt32",
                "System_Console_WriteLine_String" => "WriteLineString",
                _ => callee.UniqueName,
            };
            var isSpecialCased = !ReferenceEquals(calleeName, callee.UniqueName);

            var argVregs = new int[callee.Parameters.Length];
            for (var i = callee.Parameters.Length - 1; i >= 0; i--)
                argVregs[i] = _stack.Pop().Vreg;

            var returnType = ToIRType(callee.MethodSignature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            // Only enqueue methods we are responsible for translating; runtime
            // functions (and special-cased BCL methods) are skipped.
            if (!isSpecialCased)
                _parent.EnqueueMethod(callee);

            var defs = _builder.BuildCall(calleeName, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                _stack.Push(new StackValue(defs[0], returnType));
        }

        private void TranslateRet()
        {
            if (_function.ReturnType is VoidType)
            {
                _builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return));
                return;
            }

            var value = _stack.Pop();
            _builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        private static ILOpCode ReadOpCode(ref BlobReader ilReader)
        {
            var opCodeByte = ilReader.ReadByte();
            return (ILOpCode)(opCodeByte == 0xFE
                ? 0xFE00 + ilReader.ReadByte()
                : opCodeByte);
        }
    }

    private readonly record struct StackValue(int Vreg, IRType Type);
}
