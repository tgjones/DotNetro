using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using System.Text;

using DotNetro.Compiler.TypeSystem;

using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
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

    // Reference types newobj'd by the program. Each gets a `Vtable_<type>`
    // global; the object header (at obj-2) points at it.
    private readonly HashSet<EcmaType> _usedTypes = [];

    // Cached vtable layouts (slot index → method to dispatch). See GetVtableLayout.
    private readonly Dictionary<EcmaType, List<EcmaMethod>> _vtableLayouts = [];

    // Types whose static fields are touched (ldsfld/stsfld); their `.cctor`
    // must run at startup so the field has its initial value (e.g. ManagedHeap's
    // s_heapPtr = 0x1000). Static constructors discovered, in call order.
    private readonly HashSet<EcmaType> _staticFieldOwners = [];
    private readonly List<EcmaMethod> _staticConstructors = [];

    // Lazily-resolved ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr,
    // the runtime allocator newobj calls. Kept in C# and translated like any
    // other method (plan open-question 3 — the translated path).
    private EcmaMethod? _allocMethod;

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

        // Walk every method reachable from the entry point. Translating a method
        // can mark new types used (newobj) or pull in virtual overrides (the
        // vtable's slot methods) and static constructors, so iterate to a
        // fixpoint: drain the queue, then re-seed it from the vtables / static
        // fields discovered, until nothing new is enqueued.
        EnqueueMethod(entryPointMethod);
        do
        {
            while (_methodsToVisit.Count > 0)
                module.Functions.Add(TranslateMethod(_methodsToVisit.Dequeue()));

            EnqueueVtableSlotMethods();
            EnqueueStaticConstructors();
        } while (_methodsToVisit.Count > 0);

        // The entry function must lay out first (at `origin`) so the emulator's
        // `JSR $2000` lands on it. It performs BBC MODE 7 init, runs the static
        // constructors, calls Main, RTS — so it is built after the walk, once the
        // static-constructor list is known.
        module.Functions.Insert(0, BuildEntryFunction(entryPointMethod));

        // Append the runtime functions (oswrch, osasci, WriteLineInt32, …).
        module.Functions.AddRange(runtimeModule.Functions);

        // The whole runtime is always linked in, so the hand-written helpers
        // that reference module globals (@WriteLineBoolean → @True_String /
        // @False_String, @Beep → @Sound) are always present even when unused.
        // Register those globals unconditionally so the encoder can always
        // resolve the symbols.
        EnsureRuntimeGlobals();

        // Vtables for every newobj'd type (registered after the walk, once all
        // used types and their dispatch targets are known).
        RegisterVtableGlobals();

        // Append the string constants / static fields / vtables collected.
        module.Globals.AddRange(_globals.Values);

        return module;
    }

    internal void MarkTypeUsed(EcmaType type) => _usedTypes.Add(type);

    // ManagedHeap.Alloc(IntPtr size, IntPtr vtablePtr) → IntPtr. Resolved on
    // first newobj; kept in C# and translated through the pipeline.
    internal EcmaMethod GetAllocMethod()
    {
        if (_allocMethod != null)
            return _allocMethod;

        var managedHeapType = _typeSystemContext.RuntimeAssembly.GetType("DotNetro.Runtime", "ManagedHeap");
        _allocMethod = managedHeapType.GetMethod("Alloc",
            new MethodSignature<TypeDescription>(
                new SignatureHeader(), _typeSystemContext.IntPtr, 2, 0,
                [_typeSystemContext.IntPtr, _typeSystemContext.IntPtr]));
        return _allocMethod;
    }

    // The dispatch table for `type`: slot index → the method that index resolves
    // to for an instance of `type`. Built base-first (so inherited slots keep
    // their index) then extended with `type`'s own NewSlot virtuals; overrides
    // replace the inherited slot in place.
    //
    // Recursion stops at the root (BaseType == null, i.e. System.Object): the
    // BCL's virtuals (ToString/Equals/…) are never dispatched by the supported
    // corpus, so they get no slots. This also makes slot indices a pure function
    // of metadata — independent of which methods turn out to be "used" — so the
    // translator can emit the right offset at each callvirt without a whole-
    // program fixpoint over the used set.
    private List<EcmaMethod> GetVtableLayout(EcmaType type)
    {
        if (_vtableLayouts.TryGetValue(type, out var cached))
            return cached;

        // The root (System.Object, BaseType == null) contributes no slots: its
        // virtuals (Equals/GetHashCode/ToString/…) are never dispatched by the
        // supported corpus, and enumerating them would pull their (unsupported)
        // bodies into the program. Stopping here also makes slot indices a pure
        // function of metadata, independent of which methods turn out "used".
        List<EcmaMethod> slots;
        if (type.BaseType == null)
        {
            slots = [];
        }
        else
        {
            slots = new List<EcmaMethod>(GetVtableLayout(type.BaseType));

            foreach (var methodDefinitionHandle in type.TypeDefinition.GetMethods())
            {
                var methodDefinition = type.Assembly.MetadataReader.GetMethodDefinition(methodDefinitionHandle);
                if (!methodDefinition.Attributes.HasFlag(MethodAttributes.Virtual))
                    continue;

                var method = type.Assembly.GetMethod(methodDefinitionHandle);
                if (methodDefinition.Attributes.HasFlag(MethodAttributes.NewSlot))
                {
                    slots.Add(method);
                }
                else
                {
                    var overridden = FindOverriddenMethod(method);
                    var index = GetVtableLayout(overridden.DeclaringType).IndexOf(overridden);
                    if (index < 0)
                        throw new NotSupportedException(
                            $"IL→MIR: override {method.UniqueName} has no user vtable slot " +
                            $"(overriding a BCL virtual method is not supported yet).");
                    slots[index] = method;
                }
            }
        }

        _vtableLayouts[type] = slots;
        return slots;
    }

    // The slot index a callvirt of `callee` reads from the object's vtable.
    internal int GetVtableSlotIndex(EcmaMethod callee)
    {
        var index = GetVtableLayout(callee.DeclaringType).IndexOf(callee);
        if (index < 0)
            throw new NotSupportedException(
                $"IL→MIR: virtual method {callee.UniqueName} has no vtable slot.");
        return index;
    }

    private static EcmaMethod FindOverriddenMethod(EcmaMethod method)
    {
        var baseType = method.DeclaringType.BaseType;
        while (baseType != null)
        {
            if (baseType.TryGetMethod(method.Name, method.MethodSignature, out var overridden))
                return overridden;
            baseType = baseType.BaseType;
        }
        throw new InvalidOperationException(
            $"IL→MIR: could not find the method overridden by {method.UniqueName}.");
    }

    // Enqueue every dispatch target reachable through a used type's vtable so the
    // override bodies (e.g. Derived.MyMethod) are translated, not just the
    // statically-resolved callvirt callee.
    private void EnqueueVtableSlotMethods()
    {
        foreach (var type in _usedTypes.ToArray())
            foreach (var method in GetVtableLayout(type))
                EnqueueMethod(method);
    }

    // Enqueue the `.cctor` of each type whose static fields were touched, and
    // record it (in discovery order) so __entry can call them before Main.
    private void EnqueueStaticConstructors()
    {
        foreach (var owner in _staticFieldOwners.ToArray())
        {
            var cctor = owner.GetStaticConstructor();
            if (cctor == null || _staticConstructors.Contains(cctor))
                continue;
            _staticConstructors.Add(cctor);
            EnqueueMethod(cctor);
        }
    }

    // Emit a `Vtable_<type>` global per used type: a packed array of 2-byte
    // little-endian function pointers, one per dispatch slot (plain pointers —
    // no RTS-trick offset; the indirect-call trampoline does JMP (zp)).
    private void RegisterVtableGlobals()
    {
        foreach (var type in _usedTypes)
        {
            var name = Vtable.GetName(type);
            if (_globals.ContainsKey(name))
                continue;

            var slots = GetVtableLayout(type);
            var items = slots
                .Select(method => (MirDataItem)new DataSymbolRef(method.UniqueName))
                .ToArray();

            _globals[name] = new MirGlobal(
                SymbolName:  name,
                Type:        IRType.Pointer,
                SizeInBytes: slots.Count * PointerSize,
                Initializer: new MirInitializer(items));
        }
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
        // Remember the owner so its static constructor runs at startup.
        _staticFieldOwners.Add(field.Owner);

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

        // Run static constructors before Main (e.g. ManagedHeap's s_heapPtr =
        // 0x1000). They are void and parameterless (.cctor).
        foreach (var cctor in _staticConstructors)
            builder.BuildCall(cctor.UniqueName, []);

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
                // A managed string / object is a heap reference: a 16-bit pointer
                // on the 6502 (the pointer @ReadLine returns / @WriteLineString
                // consumes; the `this` of System.Object::.ctor, base-called by
                // every user constructor).
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => IRType.I16,
                _ => throw new NotSupportedException(
                    $"IL→MIR: primitive type {primitive.PrimitiveTypeCode} is not supported yet."),
            };
        }

        // Reference types (including arrays) are heap pointers: 16-bit on the
        // 6502. A reference-type EcmaType (class) — `this`, a class-typed field,
        // a class parameter/return — is likewise a 16-bit heap pointer.
        if (type is SZArrayType or PointerType or ByReferenceType
            || type is EcmaType { IsValueType: false })
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
        // local parameters and the entry zero-init constants. Only meaningful
        // for SSA (non-FrameSlot) locals; FrameSlot locals carry a placeholder.
        private IRType[] _localTypes = [];

        // Per-local CLR type and FrameSlot classification. A local is FrameSlot-
        // bound (lives in a .bss-style MirGlobal rather than an SSA vreg) when it
        // is a value-type struct or is address-taken via ldloca. Its FrameSlot's
        // Index equals the local index; ldloc/stloc/ldloca on it go through
        // memory (mem.frame_addr + mem.load/store, or a struct byte copy).
        private TypeDescription[] _localClrTypes = [];
        private bool[] _localIsFrameSlot = [];

        // Per-parameter FrameSlot classification. A parameter is FrameSlot-bound
        // (spilled to a .bss-style MirGlobal at function entry, read back via
        // mem.load on every ldarg) when its value is live across a call — a call
        // clobbers the caller-saved registers, so a register-held parameter would
        // not survive it. This is the caller-side ".bss" interim for cross-call
        // value preservation (notes/static-stack-alloc-plan.md, Layer 1). The
        // parameter's FrameSlot Index is _paramSlotBase + paramIndex, kept
        // disjoint from the local slots (indices 0..localCount-1).
        private bool[] _paramIsFrameSlot = [];
        private int _paramSlotBase;

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
            _localClrTypes = new TypeDescription[locals.Length];
            _localIsFrameSlot = new bool[locals.Length];

            // Address-taken locals (those with an `ldloca`), value-type (struct)
            // locals, and locals/parameters live across a call are FrameSlot-
            // bound; everything else is a pure SSA vreg. Decide upfront so
            // ldloc/stloc/ldarg know which path to take. Cross-call values must
            // live in memory because a call clobbers the caller-saved registers
            // (notes/static-stack-alloc-plan.md, Layer 1 caller-side spill).
            var addressTaken = ScanAddressTakenLocals();
            var (crossCallLocals, crossCallParams) = ComputeCrossCallLive(locals.Length);
            for (var i = 0; i < locals.Length; i++)
            {
                var clr = locals[i].Type;
                _localClrTypes[i] = clr;

                var isStruct = clr is EcmaType { IsValueType: true };
                if (isStruct || addressTaken.Contains(i) || crossCallLocals.Contains(i))
                {
                    _localIsFrameSlot[i] = true;
                    // A struct doesn't fit a single primitive IRType; size the
                    // slot in bytes via a wide IntegerType (FrameLoweringPass
                    // only reads SizeInBits/8). Address-taken primitives keep
                    // their natural width.
                    var slotType = isStruct
                        ? new IntegerType(clr.InstanceSize * 8)
                        : ToIRType(clr);
                    _function.FrameSlots.Add(new FrameSlot(
                        Index:      i,
                        Type:       slotType,
                        SymbolName: $"{_function.Name}_local{i}"));
                    _localTypes[i] = IRType.I16; // placeholder; unused for slots
                }
                else
                {
                    _localTypes[i] = ToIRType(clr);
                }
            }

            // Parameters live across a call are FrameSlot-bound too: spill the
            // incoming register value to a slot at entry and read it back from
            // memory on every ldarg (see the _paramIsFrameSlot comment). The
            // slot index lives above the local slots so the two never collide.
            _paramIsFrameSlot = new bool[_method.Parameters.Length];
            _paramSlotBase = locals.Length;
            for (var j = 0; j < _method.Parameters.Length; j++)
            {
                if (!crossCallParams.Contains(j)) continue;
                var paramClr = _method.Parameters[j].Type;
                if (paramClr is EcmaType { IsValueType: true })
                    throw new NotSupportedException(
                        $"IL→MIR: value-type parameter {j} live across a call is not " +
                        $"supported yet (method {_method.UniqueName}).");
                _paramIsFrameSlot[j] = true;
                _function.FrameSlots.Add(new FrameSlot(
                    Index:      _paramSlotBase + j,
                    Type:       ToIRType(paramClr),
                    SymbolName: $"{_function.Name}_param{j}"));
            }

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
                // FrameSlot locals live in memory (a zero-init .bss global), not
                // an SSA vreg — they are seeded by the program's own initobj /
                // stores, so skip the SSA zero-init for them.
                if (_localIsFrameSlot[i]) continue;
                var zero = _builder.BuildConstant(_localTypes[i], 0);
                _locals[i] = new StackValue(zero, _localTypes[i]);
            }

            // Spill each cross-call parameter from its incoming register vreg
            // into its FrameSlot at function entry, so later ldarg reads observe
            // a value that survives intervening calls.
            for (var j = 0; j < _paramIsFrameSlot.Length; j++)
            {
                if (!_paramIsFrameSlot[j]) continue;
                var type = ToIRType(_method.Parameters[j].Type);
                var addr = _builder.BuildFrameAddr(_paramSlotBase + j);
                _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                    new VirtualReg(addr, IsDefinition: false),
                    new VirtualReg(paramVregs[j], IsDefinition: false));
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

                    case ILOpCode.Ldloca_s: TranslateLdloca(ilReader.ReadByte()); break;

                    case ILOpCode.Ldfld:
                        TranslateLdfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Stfld:
                        TranslateStfld(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;
                    case ILOpCode.Initobj:
                        TranslateInitobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

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

                    case ILOpCode.Callvirt:
                        TranslateCallvirt(MetadataTokens.Handle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Newobj:
                        TranslateNewobj(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Dup:
                        TranslateDup();
                        break;

                    case ILOpCode.Sizeof:
                        TranslateSizeof(MetadataTokens.EntityHandle(ilReader.ReadInt32()));
                        break;

                    case ILOpCode.Conv_i:
                    case ILOpCode.Conv_u:
                        TranslateConvi();
                        break;

                    case ILOpCode.Stind_i:
                        TranslateStind(IRType.I16);
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
            _blockStartOffsets = ComputeBlockLeaders();
            foreach (var offset in _blockStartOffsets)
                _ilOffsetToBlock[offset] = _function.CreateBlock();

            DiscoverBlocksLocalParams();
        }

        // The set of basic-block leader IL offsets (offset 0, every branch
        // target, and every instruction immediately after a branch), ascending.
        // Used both by DiscoverBlocks (to create MirBlocks) and by the cross-call
        // liveness analysis, which runs before classification (hence before the
        // MirBlocks exist).
        private int[] ComputeBlockLeaders()
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

            return [.. leaders];
        }

        // Wire up the per-block local parameter vregs (the SSA/PHI substitute),
        // computed from local liveness. Split out of DiscoverBlocks so the
        // leader/block creation can be read on its own.
        private void DiscoverBlocksLocalParams()
        {
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
                    // FrameSlot locals live in memory, not SSA, so they are
                    // never threaded across edges as block parameters.
                    if (_localIsFrameSlot[localIndex]) continue;
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

        // Determine which locals and parameters are live across a call, so the
        // classifier can force them into FrameSlots (caller-side .bss spill for
        // cross-call value preservation). Runs before block discovery, hence it
        // computes block leaders itself.
        //
        // Variables are encoded into one liveness lattice: local index i maps to
        // id `i`; parameter index j maps to id `localCount + j`. A standard
        // backward liveness fixpoint yields each block's live-out set, then a
        // per-block backward instruction scan flags every variable that is live
        // at the program point immediately *after* a call instruction.
        private (HashSet<int> Locals, HashSet<int> Parameters) ComputeCrossCallLive(int localCount)
        {
            var leaders = ComputeBlockLeaders();
            int ParamVar(int j) => localCount + j;

            var useBeforeDef = new Dictionary<int, HashSet<int>>();
            var defined = new Dictionary<int, HashSet<int>>();
            var successors = new Dictionary<int, List<int>>();

            for (var bi = 0; bi < leaders.Length; bi++)
            {
                var start = leaders[bi];
                var end = bi + 1 < leaders.Length ? leaders[bi + 1] : int.MaxValue;

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

                        case ILOpCode.Ldarg_0: RecordUse(ParamVar(0), ube, def); break;
                        case ILOpCode.Ldarg_1: RecordUse(ParamVar(1), ube, def); break;
                        case ILOpCode.Ldarg_2: RecordUse(ParamVar(2), ube, def); break;
                        case ILOpCode.Ldarg_3: RecordUse(ParamVar(3), ube, def); break;
                        case ILOpCode.Ldarg_s: RecordUse(ParamVar(scan.ReadByte()), ube, def); break;
                        case ILOpCode.Ldarga_s: RecordUse(ParamVar(scan.ReadByte()), ube, def); break;
                        case ILOpCode.Starg_s: def.Add(ParamVar(scan.ReadByte())); break;

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

                if (!endsWithUncondBranch && !endsWithReturn && end != int.MaxValue)
                    succ.Add(end);

                useBeforeDef[start] = ube;
                defined[start] = def;
                successors[start] = succ;
            }

            var liveIn = new Dictionary<int, HashSet<int>>();
            var liveOut = new Dictionary<int, HashSet<int>>();
            foreach (var start in leaders)
            {
                liveIn[start] = [];
                liveOut[start] = [];
            }

            bool changed;
            do
            {
                changed = false;
                for (var bi = leaders.Length - 1; bi >= 0; bi--)
                {
                    var start = leaders[bi];

                    var outSet = liveOut[start];
                    var beforeOut = outSet.Count;
                    foreach (var s in successors[start])
                        if (liveIn.TryGetValue(s, out var sIn))
                            foreach (var v in sIn) outSet.Add(v);

                    var inSet = new HashSet<int>(useBeforeDef[start]);
                    foreach (var v in outSet)
                        if (!defined[start].Contains(v)) inSet.Add(v);

                    if (outSet.Count != beforeOut || !inSet.SetEquals(liveIn[start]))
                    {
                        liveIn[start] = inSet;
                        changed = true;
                    }
                }
            } while (changed);

            // Phase 2 — per block, walk backward from live-out. Whenever a call
            // instruction is reached, every variable live at that point (i.e.
            // live immediately after the call) is cross-call-live.
            var crossCall = new HashSet<int>();
            for (var bi = 0; bi < leaders.Length; bi++)
            {
                var start = leaders[bi];
                var end = bi + 1 < leaders.Length ? leaders[bi + 1] : int.MaxValue;

                // Collect (kind, var) effects in program order: 0=use, 1=def,
                // 2=call (var unused).
                var effects = new List<(int Kind, int Var)>();
                var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
                AdvanceTo(ref scan, start);
                while (scan.RemainingBytes > 0 && scan.Offset < end)
                {
                    var opCode = ReadOpCode(ref scan);
                    switch (opCode)
                    {
                        case ILOpCode.Ldloc_0: effects.Add((0, 0)); break;
                        case ILOpCode.Ldloc_1: effects.Add((0, 1)); break;
                        case ILOpCode.Ldloc_2: effects.Add((0, 2)); break;
                        case ILOpCode.Ldloc_3: effects.Add((0, 3)); break;
                        case ILOpCode.Ldloc_s: effects.Add((0, scan.ReadByte())); break;
                        case ILOpCode.Ldloca_s: effects.Add((0, scan.ReadByte())); break;

                        case ILOpCode.Stloc_0: effects.Add((1, 0)); break;
                        case ILOpCode.Stloc_1: effects.Add((1, 1)); break;
                        case ILOpCode.Stloc_2: effects.Add((1, 2)); break;
                        case ILOpCode.Stloc_3: effects.Add((1, 3)); break;
                        case ILOpCode.Stloc_s: effects.Add((1, scan.ReadByte())); break;

                        case ILOpCode.Ldarg_0: effects.Add((0, ParamVar(0))); break;
                        case ILOpCode.Ldarg_1: effects.Add((0, ParamVar(1))); break;
                        case ILOpCode.Ldarg_2: effects.Add((0, ParamVar(2))); break;
                        case ILOpCode.Ldarg_3: effects.Add((0, ParamVar(3))); break;
                        case ILOpCode.Ldarg_s: effects.Add((0, ParamVar(scan.ReadByte()))); break;
                        case ILOpCode.Ldarga_s: effects.Add((0, ParamVar(scan.ReadByte()))); break;
                        case ILOpCode.Starg_s: effects.Add((1, ParamVar(scan.ReadByte()))); break;

                        case ILOpCode.Call:
                        case ILOpCode.Callvirt:
                        case ILOpCode.Newobj:
                            effects.Add((2, -1));
                            scan.ReadInt32(); // consume the method/ctor token
                            break;

                        default:
                            SkipOperand(ref scan, opCode);
                            break;
                    }
                }

                var live = new HashSet<int>(liveOut[start]);
                for (var k = effects.Count - 1; k >= 0; k--)
                {
                    var (kind, var) = effects[k];
                    switch (kind)
                    {
                        case 2: // call — snapshot the live-after-call set
                            foreach (var v in live) crossCall.Add(v);
                            break;
                        case 0: // use
                            live.Add(var);
                            break;
                        case 1: // def
                            live.Remove(var);
                            break;
                    }
                }
            }

            var localsResult = new HashSet<int>();
            var parametersResult = new HashSet<int>();
            foreach (var v in crossCall)
            {
                if (v < localCount) localsResult.Add(v);
                else parametersResult.Add(v - localCount);
            }
            return (localsResult, parametersResult);
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

        private void TranslateStloc(int index)
        {
            if (!_localIsFrameSlot[index])
            {
                _locals[index] = _stack.Pop();
                return;
            }

            // FrameSlot local: store through memory.
            var value = _stack.Pop();
            var destAddr = _builder.BuildFrameAddr(index);
            if (_localClrTypes[index] is EcmaType { IsValueType: true } structType)
            {
                // Struct assignment is a byte-wise copy from the source struct
                // (value.Vreg holds its address) into this slot.
                EmitStructCopy(destAddr, value.Vreg, structType.InstanceSize);
            }
            else
            {
                // Use the local's natural CLR width, not _localTypes[index]
                // (which holds an I16 placeholder for FrameSlot locals — see the
                // classification loop). This mirrors TranslateLdloc's load width.
                var type = ToIRType(_localClrTypes[index]);
                _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                    new VirtualReg(destAddr, IsDefinition: false),
                    new VirtualReg(value.Vreg, IsDefinition: false));
            }
        }

        private void TranslateLdloc(int index)
        {
            if (_localIsFrameSlot[index])
            {
                var addr = _builder.BuildFrameAddr(index);
                if (_localClrTypes[index] is EcmaType { IsValueType: true })
                {
                    // Loading a struct local pushes its address; the value is
                    // consumed in place (ldfld) or copied on the next stloc.
                    _stack.Push(new StackValue(addr, IRType.Pointer));
                    return;
                }

                // Address-taken primitive: load the value out of the slot.
                var type = ToIRType(_localClrTypes[index]);
                var value = _function.CreateVirtualRegister(type);
                _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                    new VirtualReg(value, IsDefinition: true),
                    new VirtualReg(addr, IsDefinition: false));
                _stack.Push(new StackValue(value, type));
                return;
            }

            if (!_locals.TryGetValue(index, out var ssaValue))
                throw new NotSupportedException(
                    $"IL→MIR: ldloc of uninitialized local {index} (method {_method.UniqueName}).");
            _stack.Push(ssaValue);
        }

        // ldloca.N → push the address of the local's frame slot (a managed
        // pointer). The local was forced FrameSlot-bound during classification.
        private void TranslateLdloca(int index)
        {
            if (!_localIsFrameSlot[index])
                throw new InvalidOperationException(
                    $"IL→MIR: ldloca of non-FrameSlot local {index} (method {_method.UniqueName}).");
            var addr = _builder.BuildFrameAddr(index);
            _stack.Push(new StackValue(addr, IRType.Pointer));
        }

        private void TranslateLdarg(int index)
        {
            if (index >= _parameterVregs.Length)
                throw new NotSupportedException(
                    $"IL→MIR: ldarg {index} out of range (method {_method.UniqueName}).");
            var type = ToIRType(_method.Parameters[index].Type);

            // Cross-call parameters live in a FrameSlot: load the value back out
            // of memory rather than reusing the (clobbered) incoming register.
            if (_paramIsFrameSlot[index])
            {
                var addr = _builder.BuildFrameAddr(_paramSlotBase + index);
                var value = _function.CreateVirtualRegister(type);
                _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                    new VirtualReg(value, IsDefinition: true),
                    new VirtualReg(addr, IsDefinition: false));
                _stack.Push(new StackValue(value, type));
                return;
            }

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

        // ldfld <field> → load the field from base + offset. The base (top of
        // stack) is a pointer / managed reference / struct address (all i16).
        private void TranslateLdfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var obj = _stack.Pop();
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            var value = _function.CreateVirtualRegister(type);
            _builder.BuildInstruction(MemDialect.OpRef(LoadOpFor(type)),
                new VirtualReg(value, IsDefinition: true),
                new VirtualReg(addr, IsDefinition: false));
            _stack.Push(new StackValue(value, type));
        }

        // stfld <field> → store top-of-stack into (base + offset). Stack layout
        // (top first): value, then the object pointer / struct address.
        private void TranslateStfld(EntityHandle fieldHandle)
        {
            var field = _method.DeclaringType.Assembly.GetField(
                (FieldDefinitionHandle)fieldHandle);
            var type = ToIRType(field.Type);

            var value = _stack.Pop();
            var obj = _stack.Pop();
            var addr = EmitFieldAddress(obj.Vreg, field.Offset);
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        // initobj <type> → zero the <size> bytes at the pointer on top of stack.
        private void TranslateInitobj(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var ptr = _stack.Pop();
            var zero = _builder.BuildConstant(IRType.I8, 0);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.Fill),
                new VirtualReg(ptr.Vreg, IsDefinition: false),
                new VirtualReg(zero, IsDefinition: false),
                new Immediate(type.InstanceSize));
        }

        // Address of a field at `offset` bytes from `baseVreg` (an i16 pointer).
        // Offset 0 reuses the base directly; otherwise emit a 16-bit add of a
        // constant offset (the legalizer narrows it to byte-wise arithmetic).
        private int EmitFieldAddress(int baseVreg, int offset)
        {
            if (offset == 0) return baseVreg;
            var offsetConst = _builder.BuildConstant(IRType.I16, offset);
            var addr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.AddI),
                new VirtualReg(addr, IsDefinition: true),
                new VirtualReg(baseVreg, IsDefinition: false),
                new VirtualReg(offsetConst, IsDefinition: false));
            return addr;
        }

        // Copy `size` bytes from the struct at `srcAddr` to the struct at
        // `destAddr` (both i16 pointers), one byte at a time. Used by stloc of a
        // value-type local (an IL struct assignment / copy).
        private void EmitStructCopy(int destAddr, int srcAddr, int size)
        {
            for (var offset = 0; offset < size; offset++)
            {
                var src = EmitFieldAddress(srcAddr, offset);
                var dst = EmitFieldAddress(destAddr, offset);
                var b = _function.CreateVirtualRegister(IRType.I8);
                _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI8),
                    new VirtualReg(b, IsDefinition: true),
                    new VirtualReg(src, IsDefinition: false));
                _builder.BuildInstruction(MemDialect.OpRef(MemOp.StoreI8),
                    new VirtualReg(dst, IsDefinition: false),
                    new VirtualReg(b, IsDefinition: false));
            }
        }

        // Pre-scan the IL once for the set of locals that are address-taken
        // (have an `ldloca`). Those are forced FrameSlot-bound regardless of
        // type, alongside the value-type locals.
        private HashSet<int> ScanAddressTakenLocals()
        {
            var result = new HashSet<int>();
            var scan = _method.MethodBody!.MethodBodyBlock!.GetILReader();
            while (scan.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref scan);
                if (opCode == ILOpCode.Ldloca_s)
                    result.Add(scan.ReadByte());
                else
                    SkipOperand(ref scan, opCode);
            }
            return result;
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

        private void TranslateCall(Handle methodHandle) =>
            TranslateDirectCall(_method.DeclaringType.Assembly.ResolveMethod(methodHandle));

        // A direct (non-virtual) call: pop the args (including `this` for instance
        // methods), emit call.func, push the return value.
        private void TranslateDirectCall(EcmaMethod callee)
        {
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

            var argVregs = PopCallArgs(callee);
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

        // Pop one vreg per callee parameter (Parameters[0] is `this` for instance
        // methods), in reverse so the deepest stack entry maps to parameter 0.
        private int[] PopCallArgs(EcmaMethod callee)
        {
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
            return argVregs;
        }

        // callvirt → a direct call when the target is non-virtual (C# emits
        // callvirt for every instance call, including non-virtual ones, for its
        // null-check semantics); otherwise a vtable dispatch:
        //   %vtable : i16 = mem.load.i16 (this - 2)     ; header holds vtable ptr
        //   %fptr   : i16 = mem.load.i16 (%vtable + slot*2)
        //   call.indirect %fptr, this, args…
        private void TranslateCallvirt(Handle methodHandle)
        {
            var callee = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);

            if (!callee.IsVirtual)
            {
                TranslateDirectCall(callee);
                return;
            }

            var argVregs = PopCallArgs(callee);
            var thisPtr = argVregs[0];

            var returnType = ToIRType(callee.MethodSignature.ReturnType);
            var returnTypes = returnType is VoidType ? Array.Empty<IRType>() : [returnType];

            // The object header (the vtable pointer) sits PointerSize bytes below
            // the object reference.
            var headerAddr = EmitPointerSub(thisPtr, PointerSize);
            var vtablePtr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(vtablePtr, IsDefinition: true),
                new VirtualReg(headerAddr, IsDefinition: false));

            var slotIndex = _parent.GetVtableSlotIndex(callee);
            var slotAddr = EmitFieldAddress(vtablePtr, slotIndex * PointerSize);
            var fptr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(MemDialect.OpRef(MemOp.LoadI16),
                new VirtualReg(fptr, IsDefinition: true),
                new VirtualReg(slotAddr, IsDefinition: false));

            var defs = _builder.BuildCallIndirect(fptr, returnTypes, argVregs);
            if (returnTypes.Length > 0)
                _stack.Push(new StackValue(defs[0], returnType));
        }

        // newobj <ctor> → allocate the object then run its constructor:
        //   %ptr = call.func @ManagedHeap_Alloc(<instanceSize>, &Vtable_<type>)
        //   call.func @<ctor>(%ptr, args…)
        // and push %ptr. ManagedHeap.Alloc reserves a 2-byte header (the vtable
        // pointer) and returns a pointer to the object contents.
        private void TranslateNewobj(EntityHandle methodHandle)
        {
            var constructor = _method.DeclaringType.Assembly.ResolveMethod(methodHandle);
            var type = constructor.DeclaringType;
            _parent.MarkTypeUsed(type);

            // Constructor args, excluding the `this` at Parameters[0] (we supply
            // the freshly-allocated pointer for that). Popped before the Alloc so
            // they are evaluated in IL order; the register allocator spills any
            // that turn out to be live across the Alloc / ctor calls.
            var userArgCount = constructor.Parameters.Length - 1;
            var userArgs = new int[userArgCount];
            for (var i = userArgCount - 1; i >= 0; i--)
            {
                var arg = _stack.Pop();
                userArgs[i] = arg.Type == IRType.I1 ? MaterialiseCompare(arg.Vreg) : arg.Vreg;
            }

            var size = _builder.BuildConstant(IRType.I16, type.InstanceSize);
            var vtablePtr = _builder.BuildMemSymbol(Vtable.GetName(type));
            var allocMethod = _parent.GetAllocMethod();
            _parent.EnqueueMethod(allocMethod);
            var ptr = _builder.BuildCall(allocMethod.UniqueName, [IRType.Pointer], size, vtablePtr)[0];

            _parent.EnqueueMethod(constructor);
            var ctorArgs = new int[userArgCount + 1];
            ctorArgs[0] = ptr;
            Array.Copy(userArgs, 0, ctorArgs, 1, userArgCount);
            _builder.BuildCall(constructor.UniqueName, [], ctorArgs);

            _stack.Push(new StackValue(ptr, IRType.Pointer));
        }

        // dup → re-push the top stack value (same vreg).
        private void TranslateDup() => _stack.Push(_stack.Peek());

        // sizeof <type> → push the type's size as an i32 constant (matches the
        // IL `sizeof` result type). For IntPtr this is the 16-bit pointer size.
        private void TranslateSizeof(EntityHandle typeHandle)
        {
            var type = _method.DeclaringType.Assembly.ResolveType(typeHandle);
            var vreg = _builder.BuildConstant(IRType.I32, type.Size);
            _stack.Push(new StackValue(vreg, IRType.I32));
        }

        // conv.i / conv.u → convert the top value to a native int (i16). A wider
        // value is truncated via cast.trunc; an already-i16 value is left as-is.
        private void TranslateConvi()
        {
            var value = _stack.Pop();
            if (value.Type.Equals(IRType.I16))
            {
                _stack.Push(value);
                return;
            }

            var result = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(CastDialect.OpRef(CastOp.Trunc),
                new VirtualReg(result, IsDefinition: true),
                new VirtualReg(value.Vreg, IsDefinition: false));
            _stack.Push(new StackValue(result, IRType.I16));
        }

        // stind.i (and friends) → store the value into the pointer. Stack layout
        // (top first): value, then the destination pointer.
        private void TranslateStind(IRType type)
        {
            var value = _stack.Pop();
            var addr = _stack.Pop();
            _builder.BuildInstruction(MemDialect.OpRef(StoreOpFor(type)),
                new VirtualReg(addr.Vreg, IsDefinition: false),
                new VirtualReg(value.Vreg, IsDefinition: false));
        }

        // Address `offset` bytes *below* an i16 pointer (e.g. the object header
        // at this-2). Mirrors EmitFieldAddress but subtracts.
        private int EmitPointerSub(int baseVreg, int offset)
        {
            var offsetConst = _builder.BuildConstant(IRType.I16, offset);
            var addr = _function.CreateVirtualRegister(IRType.I16);
            _builder.BuildInstruction(ArithDialect.OpRef(ArithOp.SubI),
                new VirtualReg(addr, IsDefinition: true),
                new VirtualReg(baseVreg, IsDefinition: false),
                new VirtualReg(offsetConst, IsDefinition: false));
            return addr;
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
