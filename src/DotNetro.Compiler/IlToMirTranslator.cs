using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using System.Text;

using DotNetro.Compiler.TypeSystem;

using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
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

        // The whole runtime is always linked in, so the hand-written helpers
        // that reference module globals (@WriteLineBoolean → @True_String /
        // @False_String, @Beep → @Sound) are always present even when unused.
        // Register those globals unconditionally so the encoder can always
        // resolve the symbols.
        EnsureRuntimeGlobals();

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

    // The runtime @WriteLineBoolean references two fixed string globals,
    // @True_String / @False_String, holding "True" / "False" in the same
    // CR+NUL format @WriteLineString consumes. They are registered here (rather
    // than declared in runtime.irie) so the exact byte payload and size match
    // every other interned string. Registered on demand the first time a
    // Console.WriteLine(bool) call is translated.
    private const string TrueStringSymbol = "True_String";
    private const string FalseStringSymbol = "False_String";
    private const string SoundSymbol = "Sound";

    // The OSWORD 7 (SOUND) control block @Beep points X/Y at: four little-endian
    // 16-bit fields — channel = 1, amplitude = -15, pitch = 0, duration = 4.
    // Byte-for-byte identical to the legacy generator's `.word`/`.short` block.
    private static readonly byte[] SoundControlBlock =
        [0x01, 0x00, 0xF1, 0xFF, 0x00, 0x00, 0x04, 0x00];

    private void EnsureRuntimeGlobals()
    {
        RegisterStringGlobal(TrueStringSymbol, "True");
        RegisterStringGlobal(FalseStringSymbol, "False");

        if (!_globals.ContainsKey(SoundSymbol))
        {
            _globals[SoundSymbol] = new MirGlobal(
                SymbolName:  SoundSymbol,
                Type:        IRType.Pointer,
                SizeInBytes: SoundControlBlock.Length,
                Initializer: new MirInitializer([new DataBytes(SoundControlBlock)]));
        }
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
                // A managed string is a heap reference: a 16-bit pointer on the
                // 6502 (the pointer @ReadLine returns / @WriteLineString consumes).
                PrimitiveTypeCode.String => IRType.I16,
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
        // we can size arith ops). Runs alongside the IL semantics. Reset to
        // empty at every basic-block boundary (the current corpus keeps the
        // eval stack empty across boundaries — see the boundary assertion).
        private readonly Stack<StackValue> _stack = new();

        // Primitive SSA locals: local index → current vreg ID (and its type).
        // Re-seeded to the current block's local parameter vregs at each block
        // entry so reads observe the merged value flowing in along every edge.
        private readonly Dictionary<int, StackValue> _locals = [];

        // IL has no basic blocks; we discover them by pre-scanning for branch
        // targets and fall-through-after-branch points (see DiscoverBlocks).
        // Maps an IL offset that starts a block → the MirBlock for it.
        private readonly Dictionary<int, MirBlock> _ilOffsetToBlock = [];

        // The IL offsets that start a block, in ascending order. Used to find
        // the next block when emitting a fall-through terminator.
        private int[] _blockStartOffsets = [];

        // Per-local IRType (local index → type), used to size the per-block
        // local parameters and the entry zero-init constants.
        private IRType[] _localTypes = [];

        // Non-entry blocks carry one parameter vreg per local (in local-index
        // order) so the loop variable flows across edges as a block argument
        // (MIR's PHI substitute). Maps a block → its local parameter vregs.
        private readonly Dictionary<MirBlock, int[]> _blockLocalParams = [];

        public MethodTranslator(IlToMirTranslator parent, EcmaMethod method, MirFunction function)
        {
            _parent = parent;
            _method = method;
            _function = function;
            _builder = new MirBuilder(function);
        }

        public void Translate()
        {
            var locals = _method.MethodBody?.LocalVariables ?? [];
            _localTypes = new IRType[locals.Length];
            for (var i = 0; i < locals.Length; i++)
                _localTypes[i] = ToIRType(locals[i].Type);

            DiscoverBlocks();

            var entryBlock = _ilOffsetToBlock[0];

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

            // C# locals are zero-initialised (`.locals init`). Seed every local
            // with an explicit `arith.constant 0` of its type at function entry
            // so each local always has a definite SSA value. This makes the
            // block-parameter wiring total: every non-entry block can take a
            // parameter for every local, and every edge can supply a value.
            for (var i = 0; i < _localTypes.Length; i++)
            {
                var zero = _builder.BuildConstant(_localTypes[i], 0);
                _locals[i] = new StackValue(zero, _localTypes[i]);
            }

            var ilReader = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            _currentBlock = entryBlock;

            while (ilReader.RemainingBytes > 0)
            {
                var instructionOffset = ilReader.Offset;

                // Entering a new basic block (a discovered leader, but not the
                // entry which we already opened). Close the previous block with
                // a fall-through branch if it has no terminator, then switch.
                if (instructionOffset != 0
                    && _ilOffsetToBlock.TryGetValue(instructionOffset, out var nextBlock))
                {
                    if (!BlockHasTerminator(_currentBlock))
                        EmitBranchTo(_currentBlock, nextBlock);

                    if (_stack.Count != 0)
                        throw new NotSupportedException(
                            $"IL→MIR: non-empty eval stack at block boundary IL_{instructionOffset:X4} " +
                            $"(method {_method.UniqueName}).");

                    EnterBlock(nextBlock);
                    _currentBlock = nextBlock;
                }

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

                    case ILOpCode.Clt: TranslateClt(); break;

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
                        TranslateBr(_currentBlock, ilReader.ReadSByte() + ilReader.Offset);
                        break;
                    case ILOpCode.Br:
                        TranslateBr(_currentBlock, ilReader.ReadInt32() + ilReader.Offset);
                        break;

                    case ILOpCode.Brtrue_s:
                        TranslateCondBranch(_currentBlock, ilReader.ReadSByte() + ilReader.Offset, branchIfTrue: true);
                        break;
                    case ILOpCode.Brtrue:
                        TranslateCondBranch(_currentBlock, ilReader.ReadInt32() + ilReader.Offset, branchIfTrue: true);
                        break;
                    case ILOpCode.Brfalse_s:
                        TranslateCondBranch(_currentBlock, ilReader.ReadSByte() + ilReader.Offset, branchIfTrue: false);
                        break;
                    case ILOpCode.Brfalse:
                        TranslateCondBranch(_currentBlock, ilReader.ReadInt32() + ilReader.Offset, branchIfTrue: false);
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

        // Pre-scan the IL once to find every basic-block leader: offset 0, every
        // branch target, and every instruction immediately following a branch
        // (the not-taken fall-through). A MirBlock is created per leader; the
        // map lets branch ops resolve targets, including forward references.
        private void DiscoverBlocks()
        {
            var leaders = new SortedSet<int> { 0 };

            var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (scan.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref scan);
                int? target = null;
                switch (opCode)
                {
                    case ILOpCode.Br_s:
                    case ILOpCode.Brtrue_s:
                    case ILOpCode.Brfalse_s:
                        target = scan.ReadSByte() + scan.Offset;
                        break;
                    case ILOpCode.Br:
                    case ILOpCode.Brtrue:
                    case ILOpCode.Brfalse:
                        target = scan.ReadInt32() + scan.Offset;
                        break;
                    default:
                        SkipOperand(ref scan, opCode);
                        break;
                }

                if (target is int t)
                {
                    leaders.Add(t);
                    // The instruction after a branch starts a block (fall-through
                    // for conditionals; dead leader for unconditional br — still
                    // a boundary, harmless if unreachable).
                    if (scan.RemainingBytes > 0)
                        leaders.Add(scan.Offset);
                }
            }

            _blockStartOffsets = [.. leaders];
            foreach (var offset in _blockStartOffsets)
                _ilOffsetToBlock[offset] = _function.CreateBlock();

            // Compute which locals are live-in to each block; only those become
            // block parameters (and thus block arguments on the incoming edges).
            // Threading *all* locals everywhere would make a transient bool temp
            // (the clt result feeding brtrue) a block argument too, breaking the
            // cmpi+cond_br fusion (which needs the i1 to have exactly one use).
            var liveIn = ComputeLocalLiveness();

            foreach (var offset in _blockStartOffsets)
            {
                if (offset == 0) continue; // entry block has the method params

                var block = _ilOffsetToBlock[offset];
                var live = liveIn[offset];
                var localParams = new int[_localTypes.Length];
                for (var i = 0; i < _localTypes.Length; i++)
                    localParams[i] = -1;

                foreach (var localIndex in live)
                {
                    var vreg = _function.CreateVirtualRegister(_localTypes[localIndex]);
                    block.Parameters.Add(vreg);
                    localParams[localIndex] = vreg;
                }
                _blockLocalParams[block] = localParams;
            }
        }

        // Classic backwards local-liveness fixpoint over the discovered blocks.
        // Returns, per block-start offset, the set of local indices that are
        // live on entry (read along some path before being overwritten). Used
        // to decide which locals each block carries as parameters.
        private Dictionary<int, SortedSet<int>> ComputeLocalLiveness()
        {
            // Per block: upward-exposed uses (read before any def in the block),
            // defs (written in the block), and successor block-start offsets.
            var useBeforeDef = new Dictionary<int, HashSet<int>>();
            var defined = new Dictionary<int, HashSet<int>>();
            var successors = new Dictionary<int, List<int>>();

            for (var bi = 0; bi < _blockStartOffsets.Length; bi++)
            {
                var start = _blockStartOffsets[bi];
                var end = bi + 1 < _blockStartOffsets.Length ? _blockStartOffsets[bi + 1] : int.MaxValue;

                var ube = new HashSet<int>();
                var def = new HashSet<int>();
                var succ = new List<int>();
                var endsWithUncondBranch = false;
                var endsWithReturn = false;

                var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
                AdvanceTo(ref scan, start);
                while (scan.RemainingBytes > 0 && scan.Offset < end)
                {
                    var opCode = ReadOpCode(ref scan);
                    switch (opCode)
                    {
                        case ILOpCode.Ldloc_0: RecordUse(0, ube, def); break;
                        case ILOpCode.Ldloc_1: RecordUse(1, ube, def); break;
                        case ILOpCode.Ldloc_2: RecordUse(2, ube, def); break;
                        case ILOpCode.Ldloc_3: RecordUse(3, ube, def); break;
                        case ILOpCode.Ldloc_s: RecordUse(scan.ReadByte(), ube, def); break;
                        case ILOpCode.Ldloca_s: RecordUse(scan.ReadByte(), ube, def); break;

                        case ILOpCode.Stloc_0: def.Add(0); break;
                        case ILOpCode.Stloc_1: def.Add(1); break;
                        case ILOpCode.Stloc_2: def.Add(2); break;
                        case ILOpCode.Stloc_3: def.Add(3); break;
                        case ILOpCode.Stloc_s: def.Add(scan.ReadByte()); break;

                        case ILOpCode.Br_s:
                            succ.Add(scan.ReadSByte() + scan.Offset);
                            endsWithUncondBranch = true;
                            break;
                        case ILOpCode.Br:
                            succ.Add(scan.ReadInt32() + scan.Offset);
                            endsWithUncondBranch = true;
                            break;
                        case ILOpCode.Brtrue_s:
                        case ILOpCode.Brfalse_s:
                            succ.Add(scan.ReadSByte() + scan.Offset);
                            break;
                        case ILOpCode.Brtrue:
                        case ILOpCode.Brfalse:
                            succ.Add(scan.ReadInt32() + scan.Offset);
                            break;
                        case ILOpCode.Ret:
                            endsWithReturn = true;
                            break;
                        default:
                            SkipOperand(ref scan, opCode);
                            break;
                    }
                }

                // Fall-through successor: a block that doesn't end in an
                // unconditional branch or return flows into the next leader.
                if (!endsWithUncondBranch && !endsWithReturn && end != int.MaxValue)
                    succ.Add(end);

                useBeforeDef[start] = ube;
                defined[start] = def;
                successors[start] = succ;
            }

            var liveIn = new Dictionary<int, SortedSet<int>>();
            var liveOut = new Dictionary<int, HashSet<int>>();
            foreach (var start in _blockStartOffsets)
            {
                liveIn[start] = [];
                liveOut[start] = [];
            }

            bool changed;
            do
            {
                changed = false;
                // Iterate in reverse program order for faster convergence.
                for (var bi = _blockStartOffsets.Length - 1; bi >= 0; bi--)
                {
                    var start = _blockStartOffsets[bi];

                    var outSet = liveOut[start];
                    var beforeOut = outSet.Count;
                    foreach (var s in successors[start])
                        if (liveIn.TryGetValue(s, out var sIn))
                            foreach (var v in sIn) outSet.Add(v);

                    // liveIn = useBeforeDef ∪ (liveOut − defined)
                    var inSet = new SortedSet<int>(useBeforeDef[start]);
                    foreach (var v in outSet)
                        if (!defined[start].Contains(v)) inSet.Add(v);

                    if (outSet.Count != beforeOut || !inSet.SetEquals(liveIn[start]))
                    {
                        liveIn[start] = inSet;
                        changed = true;
                    }
                }
            } while (changed);

            return liveIn;
        }

        private static void RecordUse(int local, HashSet<int> useBeforeDef, HashSet<int> defined)
        {
            if (!defined.Contains(local))
                useBeforeDef.Add(local);
        }

        // Advance an IL reader to a given offset (block discovery helper).
        private static void AdvanceTo(ref BlobReader reader, int offset)
        {
            while (reader.Offset < offset && reader.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref reader);
                SkipBranchOrOperand(ref reader, opCode);
            }
        }

        // Like SkipOperand but also consumes branch displacement bytes (used
        // when fast-forwarding to a block start, where branches are just bytes).
        private static void SkipBranchOrOperand(ref BlobReader reader, ILOpCode opCode)
        {
            switch (opCode)
            {
                case ILOpCode.Br_s: case ILOpCode.Brtrue_s: case ILOpCode.Brfalse_s:
                    reader.ReadSByte();
                    break;
                case ILOpCode.Br: case ILOpCode.Brtrue: case ILOpCode.Brfalse:
                    reader.ReadInt32();
                    break;
                default:
                    SkipOperand(ref reader, opCode);
                    break;
            }
        }

        // On entering a discovered block, rebind every local to that block's
        // parameter vreg (the merged value) and set the builder's insertion
        // point. The eval stack is empty across boundaries (asserted by caller).
        private void EnterBlock(MirBlock block)
        {
            if (_blockLocalParams.TryGetValue(block, out var localParams))
            {
                for (var i = 0; i < localParams.Length; i++)
                    if (localParams[i] >= 0)
                        _locals[i] = new StackValue(localParams[i], _localTypes[i]);
            }
            _builder.SetInsertionPointAtEnd(block);
        }

        // Emit `cf.br target(localArgs)` passing the current SSA value of every
        // local as a block argument, in local-index order.
        private void EmitBranchTo(MirBlock from, MirBlock target)
        {
            _builder.SetInsertionPointAtEnd(from);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(target, LocalArgsFor(target)));
        }

        // Block arguments for an edge to `target`: the current value of each
        // live-in local, in ascending local-index order (matching the order
        // the target declared its parameters in DiscoverBlocks).
        private MirOperand[] LocalArgsFor(MirBlock target)
        {
            if (!_blockLocalParams.TryGetValue(target, out var localParams))
                return [];

            var args = new List<MirOperand>();
            for (var i = 0; i < localParams.Length; i++)
                if (localParams[i] >= 0)
                    args.Add(new VirtualReg(_locals[i].Vreg, IsDefinition: false));
            return [.. args];
        }

        private static bool BlockHasTerminator(MirBlock block)
        {
            if (block.Instructions.Count == 0) return false;
            var last = block.Instructions[^1];
            return DialectRegistry.ById(last.Opcode.Dialect).IsTerminator(last.Opcode.Code);
        }

        private MirBlock BlockAt(int ilOffset) =>
            _ilOffsetToBlock.TryGetValue(ilOffset, out var block)
                ? block
                : throw new InvalidOperationException(
                    $"IL→MIR: branch target IL_{ilOffset:X4} is not a block leader " +
                    $"(method {_method.UniqueName}).");

        // Advance a scanning reader past an opcode's inline operand bytes during
        // block discovery (we only care about branch displacements there).
        private static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
        {
            switch (opCode)
            {
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s:
                case ILOpCode.Stloc_s: case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s:
                case ILOpCode.Starg_s:
                    reader.ReadByte();
                    break;
                case ILOpCode.Ldc_i4: case ILOpCode.Call: case ILOpCode.Callvirt:
                case ILOpCode.Ldstr: case ILOpCode.Ldsfld: case ILOpCode.Stsfld:
                case ILOpCode.Newobj: case ILOpCode.Ldfld: case ILOpCode.Stfld:
                case ILOpCode.Ldsflda: case ILOpCode.Ldflda: case ILOpCode.Newarr:
                case ILOpCode.Box: case ILOpCode.Unbox_any: case ILOpCode.Castclass:
                case ILOpCode.Isinst: case ILOpCode.Ldtoken: case ILOpCode.Initobj:
                case ILOpCode.Sizeof: case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn:
                    reader.ReadInt32();
                    break;
                case ILOpCode.Ldc_i8:
                    reader.ReadInt64();
                    break;
                case ILOpCode.Ldc_r4:
                    reader.ReadSingle();
                    break;
                case ILOpCode.Ldc_r8:
                    reader.ReadDouble();
                    break;
                case ILOpCode.Switch:
                    var n = reader.ReadUInt32();
                    for (var i = 0; i < n; i++) reader.ReadInt32();
                    break;
                default:
                    break;
            }
        }

        private int[] _parameterVregs = [];

        // The block currently being emitted into. Normally tracks the IL
        // basic-block leader, but a value-used compare (TranslateClt's diamond)
        // splits the current block and redirects this to the merge block so
        // subsequent IL emits after the materialised 0/1 result.
        private MirBlock _currentBlock = null!;

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

        // br / br.s → cf.br to the target block, passing current locals as args.
        private void TranslateBr(MirBlock from, int target)
        {
            EmitBranchTo(from, BlockAt(target));
        }

        // clt → arith.cmpi <slt>, %a, %b → i1, pushed onto the eval stack.
        // (cge, used by bge, would be sge — added when a test needs it.)
        //
        // The cmpi is left unmaterialised: if an immediately-following
        // brtrue/brfalse consumes it, TranslateCondBranch builds a cf.cond_br on
        // it and Irie's isel fuses the pair (loop-condition path). If instead the
        // i1 is consumed as a *value* (e.g. passed to Console.WriteLine(bool)),
        // TranslateCall materialises it into a concrete 0/1 i8 via a cond_br
        // diamond (see MaterialiseCompare).
        private void TranslateClt()
        {
            var right = _stack.Pop();
            var left = _stack.Pop();
            if (!left.Type.Equals(right.Type))
                throw new NotSupportedException(
                    $"IL→MIR: clt of mismatched types {left.Type} and {right.Type}.");

            var result = _builder.BuildCmpI(ArithCmpPredicate.Slt, left.Vreg, right.Vreg);
            _stack.Push(new StackValue(result, IRType.I1));
        }

        // Build the cond_br diamond that turns an i1 compare result (defined by a
        // preceding arith.cmpi at the end of the current block) into a concrete
        // 0/1 i8:
        //
        //   <current>:                              ; cmpi already emitted here
        //       %c : i1 = arith.cmpi <pred>, %a, %b
        //       cf.cond_br %c, trueLeg, falseLeg     ; appended here
        //   trueLeg:  cf.br merge(1 : i8)
        //   falseLeg: cf.br merge(0 : i8)
        //   merge(%r : i8):                         ; becomes the new current block
        //
        // The cmpi+cond_br pair is selected by isel exactly as the fused branch
        // path. Returns the merge block's i8 parameter vreg (the 0/1 result).
        private int MaterialiseCompare(int condVreg)
        {
            var trueLeg = _function.CreateBlock();
            var falseLeg = _function.CreateBlock();
            var merge = _function.CreateBlock();

            var resultVreg = _function.CreateVirtualRegister(IRType.I8);
            merge.Parameters.Add(resultVreg);

            // cond_br on the existing cmpi result, at the end of the current block.
            _builder.SetInsertionPointAtEnd(_currentBlock);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.CondBr),
                new VirtualReg(condVreg, IsDefinition: false),
                new BlockTarget(trueLeg, []),
                new BlockTarget(falseLeg, []));

            // trueLeg → merge(1 : i8)
            _builder.SetInsertionPointAtEnd(trueLeg);
            var one = _builder.BuildConstant(IRType.I8, 1);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(merge, [new VirtualReg(one, IsDefinition: false)]));

            // falseLeg → merge(0 : i8)
            _builder.SetInsertionPointAtEnd(falseLeg);
            var zero = _builder.BuildConstant(IRType.I8, 0);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(merge, [new VirtualReg(zero, IsDefinition: false)]));

            // Subsequent IL emits into the merge block.
            _builder.SetInsertionPointAtEnd(merge);
            _currentBlock = merge;
            return resultVreg;
        }

        // brtrue/brfalse → cf.cond_br on the i1 condition. The taken edge goes
        // to the branch target; the not-taken edge falls through to the next
        // block (the instruction immediately after the branch — a discovered
        // leader). Irie's isel fuses a preceding arith.cmpi into the cond_br.
        private void TranslateCondBranch(MirBlock from, int target, bool branchIfTrue)
        {
            var cond = _stack.Pop();

            var fallThrough = NextBlockAfter(from);
            var targetBlock = BlockAt(target);

            // brtrue: cond true → target, false → fall-through.
            // brfalse: invert the two edges.
            var trueEdge = branchIfTrue ? targetBlock : fallThrough;
            var falseEdge = branchIfTrue ? fallThrough : targetBlock;

            _builder.SetInsertionPointAtEnd(from);
            _builder.BuildInstruction(
                CfDialect.OpRef(CfOp.CondBr),
                new VirtualReg(cond.Vreg, IsDefinition: false),
                new BlockTarget(trueEdge, LocalArgsFor(trueEdge)),
                new BlockTarget(falseEdge, LocalArgsFor(falseEdge)));
        }

        // The block that starts at the next leader offset after `block` — the
        // fall-through successor of a conditional branch.
        private MirBlock NextBlockAfter(MirBlock block)
        {
            // Find block's start offset, then the next leader offset.
            foreach (var (offset, b) in _ilOffsetToBlock)
            {
                if (!ReferenceEquals(b, block)) continue;
                var idx = Array.IndexOf(_blockStartOffsets, offset);
                if (idx >= 0 && idx + 1 < _blockStartOffsets.Length)
                    return _ilOffsetToBlock[_blockStartOffsets[idx + 1]];
                break;
            }
            throw new InvalidOperationException(
                $"IL→MIR: conditional branch in a block with no fall-through successor " +
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
                "System_Console_WriteLine_Boolean" => "WriteLineBoolean",
                "System_Console_ReadLine" => "ReadLine",
                "System_Console_Beep" => "Beep",
                _ => callee.UniqueName,
            };
            var isSpecialCased = !ReferenceEquals(calleeName, callee.UniqueName);

            var argVregs = new int[callee.Parameters.Length];
            for (var i = callee.Parameters.Length - 1; i >= 0; i--)
            {
                var arg = _stack.Pop();
                // An i1 compare result passed as a value (rather than consumed by
                // a branch) is materialised into a concrete 0/1 i8 here, via the
                // cond_br diamond. The argument's expected type is i8 (Boolean
                // maps to i8 in ToIRType), so the materialised i8 matches.
                argVregs[i] = arg.Type == IRType.I1
                    ? MaterialiseCompare(arg.Vreg)
                    : arg.Vreg;
            }

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
