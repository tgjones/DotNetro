# DotNetro → Irie coverage plan

Expand Irie's MIR vocabulary, lowering pipeline, and machine-code emitter
so it can handle every IL operation used by the active tests in
[`src/DotNetro.Compiler.Tests/DotNetCompilerTests.cs`](../src/DotNetro.Compiler.Tests/DotNetCompilerTests.cs).
This is a precursor to switching `DotNetro.Compiler` away from the direct
`M6502CodeGenerator` backend onto Irie.

The DotNetro frontend itself (IL reader, type system, vtable tracker,
method discovery) is **out of scope** here — this plan only grows Irie so
that *when* the frontend starts emitting Irie MIR, every op it produces
can already be lowered all the way to bytes.

The plan is structured as:

1. Test inventory — IL ops needed, grouped by test
2. MIR vocabulary additions
3. Module-level extensions
4. Lowering pipeline per op
5. Machine-code emit table extensions
6. Stepwise landing order (one slice per PR)
7. Open questions / deferred work

Planning decisions made up front (recorded for traceability):

| Decision | Choice |
|---|---|
| Addressed locals (`ldloca`, structs) | Static `.bss` slot per local. Frontend throws `NotImplementedException` when the call-graph SCC analysis finds recursion. |
| Call shape | One `call.func` op: defs = return-value vregs, uses = arg vregs + `Symbol` callee. `CallLowering.LowerCall` inserts the physreg copies (mirrors today's `LowerFormalArguments` / `LowerReturn`). |
| Cmp + branch | Fused at instruction selection: `arith.cmpi <pred>` + `cf.cond_br` → `mos6502.cmp` + `mos6502.bXX`. The i1 condition vreg never reaches RA. |

---

## 1. Test inventory

Each row lists the active `[CompilerTest]` and the IL ops its body uses
(after the IL reader is done with the source). "BCL" calls are
special-cased today via `CompileMethod` in
[`DotNetCompiler.cs`](../src/DotNetro.Compiler/DotNetCompiler.cs);
they remain hand-written even after the switch.

| Test | IL ops | BCL call |
|---|---|---|
| `PrintHelloWorld` | `ldstr`, `call` | `Console.WriteLine(string)` |
| `Print42` | `ldc.i4.s`, `call` | `Console.WriteLine(int)` |
| `PrintMinus1` | `ldc.i4.m1`, `call` | `Console.WriteLine(int)` |
| `AddInt32` | `ldc.i4.1`, `stloc.0`, `stloc.1`, `ldloc.0`, `ldloc.1`, `add`, `call` | `Console.WriteLine(int)` |
| `CompareLessThanInt32` | `ldc.i4.1`, `ldc.i4.2`, `ldloc.*`, `stloc.*`, `clt`, `call` | `Console.WriteLine(bool)` |
| `CallMethodWithParameter` | `ldc.i4.s 16`, `call` | — |
| `CallMethodWithReturnValue` | `call`, `call` | `Console.WriteLine(int)` |
| `CallMethodWithParameterAndReturnValue` | `ldc.i4.s 44`, `call`, `call` | — |
| `CallMethodWithTwoParametersAndReturnValue` | `ldc.i4.1/2`, `call`, `call` | — |
| `ReadAndWriteLine` | `ldstr`, `call`, `stloc`, `ldloc` | `Console.WriteLine`, `Console.ReadLine`, `Console.Beep` |
| `UseStruct` | `ldloca.s`, `initobj`, `ldc.i4`, `stfld`, `ldfld`, `add`, `call` | `Console.WriteLine(int)` |
| `UseStaticFields` | `ldc.i4`, `stsfld`, `ldsfld`, `add`, `call` | `Console.WriteLine(int)` |
| `ForLoop` | `ldstr`, `call`, `ldc.i4.0`, `stloc.0`, `br.s`, `ldloc.0`, `ldc.i4.5`, `blt.s`, `add`, `call` | `Console.WriteLine(int)`, `Console.WriteLine(string)` |
| `CallNestedMethods` | `call`, multi-level | `Console.WriteLine(int)` |
| `UseClass` | `newobj`, `dup`, `stfld`, `ldfld`, `add`, `call` | `Console.WriteLine(int)` |
| `UseNestedClass` | `newobj`, `dup`, `stfld`, nested `ldfld`, `add`, `call` | `Console.WriteLine(int)` |
| `UseNestedClassWithConstructors` | `newobj`(with args), `stfld`, `ldfld`, `add`, `call` | `Console.WriteLine(int)` |
| `UseStructWithConstructor` | `ldloca.s`, `call` (ctor), `ldfld`, `add`, `call` | `Console.WriteLine(int)` |
| `CallInstanceMethodOnStruct` | `ldloca.s`, `call` (ctor), `call` (instance) | `Console.WriteLine(int)` |
| `CallInstanceMethodOnClass` | `newobj`, `call` (instance) | `Console.WriteLine(int)` |
| `CallOverriddenMethodOnClass` | `newobj`, `callvirt` | `Console.WriteLine(int)` |

### Distinct IL ops in scope

```
add  blt.s  bge.s  br.s  brfalse.s  brtrue.s  call  callvirt  clt
conv.i  dup  initobj  ldarg.0..3  ldc.i4{,_0..5,_m1,_s,_}  ldfld
ldloc.0..2  ldloca.s  ldsfld  ldstr  newobj  nop  ret  sizeof  stfld
stind.i  stloc.0..2  stsfld
```

Note: every `bge.s` / `blt.s` is decomposed into `clt`+`brtrue` /
`cge`+`brtrue` by `CompileBge` / `CompileBlt`, so only `cge` / `clt`
(plus `brtrue` / `brfalse`) need first-class treatment.

---

## 2. MIR vocabulary additions

### 2.1 `arith` dialect — extend in place

Existing: `addi`, `subi`, `cmpi`, `addi_with_carry`, `constant`.

Add:

```
%lo, %bout = arith.subi_with_borrow %a, %b, %bin
```

Post-legalization narrow form of multi-byte subtract. 2 defs (result,
borrow-out) + 3 uses (a, b, borrow-in). Same shape as `addi_with_carry`
so the legalizer's chain machinery applies unchanged.

Generalise existing ops:

- **`arith.constant`** must accept any `IntegerType`, not just i1. The
  legalizer narrows `arith.constant <N> : i32` → four `arith.constant
  <byte> : i8` joined by `pseudo.merge`, same NarrowScalar shape used
  for `addi`.
- **`arith.cmpi`** needs a stable predicate encoding. Plan: store
  `ArithCmpPredicate` as the first `Immediate` operand. Operand layout
  becomes `def[0]=i1, use[0]=pred-imm, use[1]=a, use[2]=b`. `MirWriter`
  / `MirParser` render the immediate symbolically (`slt`, `sge`, …).
  Resolves [unified-ir-plan.md](unified-ir-plan.md) open question #1.

### 2.2 New `mem` dialect

Address-typed pointers are i16 on MOS6502 (`IRType.Pointer` already
aliases `I16`). The dialect operates on those.

```
%v : <ty> = mem.load <ty> %addr            ; def: vreg of ty, use: i16 addr
            mem.store <ty> %addr, %val     ; uses: i16 addr, vreg of ty
%p : i16  = mem.symbol @name               ; def only; carries Symbol("name") operand
            mem.fill %addr, %byte, %count  ; zero/byte fill (initobj)
            mem.copy %dst, %src, %count    ; struct copies (stfld of value-type field)
```

Encoding decisions:

- `mem.load` / `mem.store` carry the element type as an immediate
  encoded in the opcode subkind. Initially: separate enum cases per
  size (`MemOp.LoadI8`, `LoadI16`, `LoadI32`) — keeps parser /
  legalizer rules simple. Generalising to a width operand can wait.
- `mem.symbol` is the carrier for any module-level address: string
  literal, static field, vtable, function (for indirect calls), and
  static frame slot. The actual Symbol operand is the lone operand;
  no second indirection needed.
- `mem.fill` / `mem.copy` use `Immediate` count operands when known at
  compile time (almost always — IL `initobj` knows the type size).

`MemDialect` registers via `MirBootstrap.EnsureRegistered`.

### 2.3 New `call` dialect (or extend `cf`)

Pick `call` for clarity, since `cf` is currently "branches only".

```
%r0, %r1, ... = call.func     @callee,  %arg0, %arg1, ...
                ; defs: 0..N return-value vregs (typed)
                ; uses: Symbol("callee") + arg vregs

%r0, %r1, ... = call.indirect %target,  %arg0, %arg1, ...
                ; defs: 0..N return-value vregs (typed)
                ; uses: i16 target pointer (vreg) + arg vregs
```

**No virtual-call op.** Concepts like vtables, vtable-slot offsets,
and `this`-pointer adjustment live in the DotNetro frontend (mirroring
the Clang → LLVM IR separation: Clang lowers `virtual` calls to
explicit pointer arithmetic + indirect call before handing off to
LLVM). The frontend handles `Callvirt` by emitting:

1. `mem.load i16 %this + <vtable_ptr_offset>` to fetch the vtable.
2. `mem.load i16 %vtable + <slot_offset>` to fetch the method address.
3. `call.indirect %method_addr, %this, %args...`.

Keeping MIR's call vocabulary at "direct" and "through a pointer" lets
Irie stay neutral about the source language's type system. Any
language-specific dispatch (Java itables, C++ multi-inheritance
thunks, .NET interface dispatch via maps, …) is the frontend's job to
lower to those two primitives.

Operand encoding: defs come first (the convention `MirInstruction`
already enforces). For `call.func`, the callee `Symbol` is the first
use; args follow. For `call.indirect`, the target pointer vreg is the
first use; args follow.

`call.func` / `call.indirect` are terminators? **No** — control
returns to the next instruction. They are *not* in `_terminators`.
But for RA purposes they are clobber barriers: every caller-saved
register must be assumed dead across them. This is encoded via
implicit defs of those physregs by the time RA sees them (added by
AbiLowering's `LowerCall`).

### 2.4 `pseudo` dialect — extend in place

Add structural byte-level extraction/insertion ops, modelled on LLVM's
`G_EXTRACT` / `G_INSERT`:

```
%byte : i8  = pseudo.extract %wide, <bit_offset>     ; bit_offset is Immediate
%new  : iK  = pseudo.insert  %wide, %byte, <bit_offset>
```

Both ops carry an `Immediate` operand giving the bit offset (a
multiple of 8 in practice; expressed in bits to match LLVM and to
leave room for sub-byte cases). Used to:

- Address the low/high byte of an i16 (e.g. an i16 produced by
  `mem.symbol`) without inventing a `SymbolHalf` operand kind.
  Replaces what would otherwise be a `pseudo.unmerge` followed by
  picking a specific def — clearer when only one byte is wanted.
- Combine multi-byte intermediate results without needing a full
  `pseudo.merge` (which insists on naming every part).

`pseudo.extract` / `pseudo.insert` are artifact ops (like `merge` /
`unmerge`): the legalizer's `LegalizationArtifactCombiner` is
extended to fold `extract(merge(...))`, `extract(insert(...))`, etc.

### 2.5 New `cast` dialect

IL has a family of conversion ops (`conv.i`, `conv.i1`, `conv.i2`,
`conv.u4`, …). The frontend must not synthesise `pseudo.unmerge` /
`pseudo.extract` directly — those are post-legalization artifacts.
The frontend's conversions are *typed*: an i32-to-i16 truncation has
semantic meaning that should be visible in MIR text.

Add a `cast` dialect:

```
%y : iM = cast.trunc <iM> %x : iN     ; M < N
%y : iN = cast.zext  <iN> %x : iM     ; M < N, zero-extend
%y : iN = cast.sext  <iN> %x : iM     ; M < N, sign-extend
```

Initial scope for the test set: only `cast.trunc` is needed (IL
`Conv.I` lowers an i32 to an i16 intptr on a 6502 target — see
`CompileConvi` in `DotNetCompiler.cs`). `zext` / `sext` are listed
for completeness; they're added when a test needs them.

Lowering:

| Pass | Action |
|---|---|
| Legalizer | `cast.trunc iM ← iN` is rewritten to `pseudo.unmerge` of the source vreg, picking the low M/8 byte vregs (or to `pseudo.extract` if M = 8). `cast.zext` / `cast.sext` are rewritten to `pseudo.merge` of the source bytes with explicit `arith.constant 0` (zext) or sign-replicated bytes (sext) for the upper bytes. |

After legalization, MIR's vocabulary collapses back to the `pseudo.*`
artifacts the rest of the pipeline already handles — `cast.*` exists
purely so the frontend has a typed conversion to emit.

### 2.6 New static-frame model

Add `MirFunction.FrameSlots : List<FrameSlot>` where `FrameSlot` is:

```csharp
public sealed record FrameSlot(int Index, IRType Type, string SymbolName);
```

`SymbolName` is computed once at slot creation as
`$"{function.Name}_local{index}"` and is the label the back-end emits
into a `.bss`-style data section. The frontend allocates one slot per
IL local whose address is taken (detected by `ldloca` / `ldflda`) and
per IL local of value-type. Plain scalar locals stay as SSA vregs.

A new `mem.frame_addr <slot_index>` op surfaces a slot's address:

```
%p : i16 = mem.frame_addr 0
```

This lowers to `mem.symbol @<function_name>_local0` at a late stage
(any pass between PhiElimination and ISel — see §4.5). Modelling it
separately at the MIR level keeps the frame layout decision in one
place (`MirFunction.FrameSlots`) and lets MIR text dump the slot index
rather than a synthesised name.

### 2.7 Non-reentrance guard

Add a frontend-side analysis (in `DotNetro.Compiler`, *not* in Irie)
that runs over the call graph after methods are discovered. Uses
Tarjan SCC. Any SCC of size > 1, or any single-node SCC with a
self-edge, causes:

```csharp
throw new NotImplementedException(
    "DotNetro frontend: recursion is not yet supported. Static frame slots " +
    "assume non-reentrant functions. Methods in cycle: <list>.");
```

This lives in the frontend because it's a frontend constraint: it's
the frontend's `mem.frame_addr → mem.symbol` lowering that assumes
non-reentrance. Irie itself stays neutral.

---

## 3. Module-level extensions

Today `MirModule` has only `Functions`. The tests need one more kind
of module-level data: **globals**. Strings, static fields, vtables,
and `.bss` frame slots are all instances of the same concept (a named
region of memory, possibly with an initializer).

```csharp
public sealed class MirModule
{
    public List<MirFunction> Functions { get; }
    public List<MirGlobal>   Globals   { get; }   // NEW
}

public sealed record MirGlobal(
    string             SymbolName,
    IRType             Type,
    int                SizeInBytes,
    MirInitializer?    Initializer);   // null = zero-init (.bss)

// Initializer model — one or more data items concatenated.
public sealed record MirInitializer(MirDataItem[] Items);

public abstract record MirDataItem;
public sealed   record DataBytes(byte[] Bytes)                : MirDataItem;
public sealed   record DataSymbolRef(string SymbolName)       : MirDataItem;  // 2 bytes on MOS6502
```

This single shape covers every test:

- **String constant** — `MirGlobal("hello", i8[14], 14, Initializer([DataBytes("Hello, World!\n"u8)]))`
- **Static field (uninitialized `.bss`)** — `MirGlobal("MyStruct_A", i32, 4, Initializer: null)`
- **Vtable** — `MirGlobal("MyClass_vtable", i16[2], 4, Initializer([DataSymbolRef("MyClass_ctor"), DataSymbolRef("MyClass_MyMethod")]))`
- **Frame slot** — `MirGlobal("UseStruct_local0", <local-struct-type>, 8, Initializer: null)`

Persistence:

- `MirWriter` emits each global before `Functions`, e.g.
  ```
  global @hello : [14 x i8] = bytes "Hello, World!\n"
  global @MyStruct_A : i32                                            ; zero-init
  global @MyClass_vtable : [2 x i16] = [@MyClass_ctor, @MyClass_MyMethod]
  global @UseStruct_local0 : i64                                      ; frame slot, zero-init
  ```
- `MirParser` round-trips them. Same two-pass scope rule as functions:
  scan all module-level decls first, then resolve `Symbol` references
  during instruction parsing.
- `MirBinaryWriter` / `MirBinaryReader` add one tagged section for
  globals; the discriminated `MirDataItem` is encoded per-item.

Frame slots are not module-level *in MIR text* — they live on
`MirFunction.FrameSlots` while a function is being constructed and
get materialised as `MirGlobal` entries by `FrameLoweringPass`
(see §4.5). They appear in MIR text only after that pass runs.

---

## 4. Lowering pipeline per op

This section walks each new MIR op through the seven existing passes
(see [unified-ir-plan.md](unified-ir-plan.md)) plus AMS and machine
code emit.

### 4.1 `arith.constant <N> : iK`

| Pass | Action |
|---|---|
| Legalizer | `iK > 8`: `NarrowScalar` to per-byte `arith.constant : i8` joined by `pseudo.merge`. |
| ISel (MOS6502) | `arith.constant 0 : i1` → `mos6502.clc`; `1 : i1` → `mos6502.sec`. `arith.constant N : i8` → fresh vreg in class `Anyi8` with a `pseudo.copy <imm>` def. |
| PseudoExpansion | `pseudo.copy <imm>` already handles A/X/Y immediate loads. Extend `MOS6502PseudoExpander.ExpandImmediateToReg` for zp destinations: emit `lda.imm #N ; sta.zp $zpK`. |
| Emit table | Already covers `LdaImm` (need to add it — see §5). |

### 4.2 `arith.subi <iK>` and `arith.subi_with_borrow <i8>`

| Pass | Action |
|---|---|
| Legalizer | `iK > 8`: `NarrowScalar` to chain of `subi_with_borrow i8`. Chain head's borrow-in is `arith.constant 1 : i1` (SBC chain starts with carry set, i.e. no borrow). |
| ISel | `arith.subi_with_borrow i8` → `mos6502.sbc` (pre-AMS). 2 defs (result, borrow-out), 3 uses (a tied to def[0], b, borrow-in). Same DialectInstructionInfo shape as `Adc` already in `MOS6502Dialect.AdcInfo`. |
| AMS | Add `Sbc → SbcZp/SbcImm` refinement, mirroring the existing `Adc` rule in `MOS6502AddressingModeSelectorPass.RefineAdc`. |
| Emit table | Add `SbcZp`, `SbcImm` rows. |

### 4.3 `arith.cmpi <pred> %a, %b` + `cf.cond_br`

| Pass | Action |
|---|---|
| Legalizer | `cmpi iK` for `K > 8`: chain of `cmpi i8` on byte pairs, top-down (high byte first). For unsigned: only `Z`/`C` matter; for signed: also `N`/`V`. First cut: support `Eq`, `Ne`, `Slt`, `Sge` (covers `clt`/`cge`); defer `Sle`, `Sgt`, unsigned predicates. |
| ISel | Pattern-match `arith.cmpi <pred> %a, %b` followed by `cf.cond_br %cond, T, F` in the same block, with `%cond` having exactly one use (the cond_br). Emit `mos6502.cmp %a, %b` (def `$n, $z, $c`) + `mos6502.b<pred> T` + `mos6502.jmp.abs F`. Remove the `cmpi` and `cond_br`. |
| ISel fallback (no fusion) | `cmpi` whose result is used elsewhere (rare; not in current tests) → materialise i1 via `cmp + branch + lda.imm 0/1`. **Defer**, throw with a clear message instead. |
| AMS | Add `Cmp → CmpZp / CmpImm` refinement. |
| PseudoExpansion | Synthesise `mos6502.blt` / `mos6502.bge` (already have `bgt`): both expand to a pair of real branches based on $n and $v. Use the standard 6502 idioms (`bmi`/`bpl` after CMP when V is known clear, which it is after a simple CMP). |
| Emit table | Add `CmpZp`, `CmpImm`, `Beq`, `Bne`, `Bmi`, `Bpl`, `JmpAbs` rows. (`Bgt` is gone by emit time; same for `Blt`/`Bge`.) |

### 4.4 `mem.load`, `mem.store`, `mem.symbol`

| Pass | Action |
|---|---|
| Legalizer | `mem.load/store i32` and `i16` → chain of i8 loads/stores at consecutive offsets. The address operand is shared across the chain; offsets are encoded by emitting an `arith.constant + arith.addi` to bump the address per byte, *unless* the offset is a small constant (≤ 255) and isel can fold it into an addressing mode. First cut: emit `mem.load.byte_at %p, <offset>` (new pre-AMS op) with `<offset>` as `Immediate`. |
| ISel | `mem.load.byte_at %p, <off>` → `mos6502.lda` with the address vreg in zero page (the `Imag8` class). AMS picks indirect-Y mode if `<off>` is non-zero and a Y register can be loaded; for first cut, just generate `ldy.imm <off>` + `lda.indy $p`. |
| Legalizer (`mem.symbol`) | Stays as a single op producing an i16 value. Other passes treat the def like any other i16 vreg (e.g. when it flows into `mem.load`, the i16 address is `pseudo.unmerge`d into two i8 bytes that live in zero page). |
| `pseudo.extract` of `mem.symbol` | When the legalizer narrows i16 ops, the artifact combiner sees `pseudo.extract %addr, <byte_idx>` where `%addr` is defined by `mem.symbol @name`. The combiner rewrites the extract to a target-dialect immediate load (`mos6502.lda.imm.symlo @name` / `mos6502.lda.imm.symhi @name`) — two distinct opcodes that both emit the LDA-immediate byte (`0xA9`) but with different emit-time operand encodings (`<@name` vs `>@name`). The Symbol travels as a `Symbol` operand on the target instruction; no generic-MIR operand variant is needed. |
| Emit table | Add `Ldy.Imm`, `LdaIndY`, `StaIndY`, `LdaImmSymLo`, `LdaImmSymHi` rows. The last two use new `EmitOperandKind.SymbolLowByte` / `SymbolHighByte` rules that read a `Symbol` operand and emit `MachineCodeOperand.ExternalRef` plus a half-tag for the assembler. |

### 4.5 `mem.frame_addr <slot>`

| Pass | Action |
|---|---|
| New target-agnostic pass `FrameLoweringPass` (between PhiElimination and TwoAddressInstruction) | For each `mem.frame_addr <i>`, replace with `mem.symbol @<func>_local<i>`. Also register the slot's symbol with the module's `Globals` list (with the slot's IRType / size). |
| Rest | Same as `mem.symbol` above. |

This is the moment where `MirFunction.FrameSlots` becomes
`MirModule.Globals` entries. Doing it as its own pass keeps the
abstraction visible in MIR text up to this point.

### 4.6 `mem.fill` / `mem.copy`

| Pass | Action |
|---|---|
| Legalizer | Small known counts (`Immediate` count ≤ ~16): unroll into per-byte `mem.store`. Larger counts: leave as-is and let isel emit a loop. (Current tests use sizes ≤ 8.) |
| ISel | Unrolled form is handled by the per-byte `mem.store` rules. |

### 4.7 `call.func`

| Pass | Action |
|---|---|
| AbiLowering — extend `CallLowering` with `LowerCall` | For each arg vreg (in CC_MOS order, byte-LSB-first): emit `pseudo.unmerge` + `pseudo.copy → physreg`. For each return-value def: emit `pseudo.copy ← physreg` and `pseudo.merge`. Replace the `call.func` with `mos6502.jsr.abs @callee` (or a generic `cf.jsr` — TBD; see §7), with explicit-use physregs for args, explicit-def physregs for return values, and implicit-defs for every caller-saved physreg not already covered. |
| ISel | `mos6502.jsr.abs @callee` passes through (target dialect). The synthesised `pseudo.copy`s are handled by existing rules. |
| RA | Already understands implicit defs as clobber barriers — they shorten live ranges through the call. |
| Emit table | Add `JsrAbs` row (`AbsoluteAddress` kind, takes the `Symbol` operand). |

### 4.8 `call.indirect`

The frontend has already done all the type-system work by the time
this op appears in MIR: vtable lookup, `this`-pointer adjustment,
itable/imap dispatch — all lowered to `mem.load` chains producing an
i16 function pointer. The MIR side just sees a function-pointer
vreg and the args.

| Pass | Action |
|---|---|
| AbiLowering | Same as `call.func` (§4.7): for each arg, emit `pseudo.unmerge` + `pseudo.copy → physreg`; for each return-value def, emit `pseudo.copy ← physreg` and `pseudo.merge`. Replace the `call.indirect` with `mos6502.jsr.ind <ptr-bytes>` (or an equivalent shim — see below). Implicit-defs for caller-saved physregs as in `call.func`. |
| ISel | The 6502 has no native `JSR (indirect)`. Lower to a small thunk: store the i16 pointer into a fixed zero-page address `__call_target`, then `mos6502.jsr.abs @__call_indirect_trampoline`, where the trampoline is a hand-written runtime helper (`runtime.mir`) doing `JMP (__call_target)`. The trampoline itself is `mos6502.jmp.ind $<__call_target>`. |
| Emit table | Add `JmpInd` row (used by the trampoline body only). |

Frontend responsibility (for context, not MIR-level):

```
; Frontend lowers IL `callvirt MyMethod` to MIR:
%vtable     : i16 = mem.load i16 %this + <vtable_field_offset>
%method_addr: i16 = mem.load i16 %vtable + <slot_offset>
%r0, %r1    = call.indirect %method_addr, %this, %args...
```

The frontend computes `<vtable_field_offset>` and `<slot_offset>` from
its type system (`VtableTracker.GetVtableSlotLabel`,
`TypeSystemContext`, etc.) before any MIR is built — MIR never sees the
type system.

### 4.9 `cf.br`, `cf.cond_br` (existing)

Already in scope from earlier work. The only missing piece for the
test set is: when `cf.cond_br` is *not* fused with a preceding
`arith.cmpi` (e.g. `brtrue %b` where `%b` is a stored bool), we need a
fallback ISel rule. First cut: throw `NotSupportedException` —
inspection shows none of the active tests hit this path (`brtrue` /
`brfalse` always follow a comparison). Promote when a test needs it.

### 4.10 Console BCL methods

`Console.WriteLine(int)`, `Console.WriteLine(string)`,
`Console.WriteLine(bool)`, `Console.ReadLine`, `Console.Beep` are
**not compiled from .NET BCL IL** — the existing frontend intercepts
them (search `_codeGenerator.CompileSystemConsole*`). Plan:

- Keep these as hand-written MIR-level helper functions in a small
  `runtime.mir` file (in `DotNetro/Runtime/` next to the existing
  C# runtime). The frontend lowers a `call System.Console.WriteLine`
  to `call.func @Sys_Console_WriteLine_Int32`, etc.
- `runtime.mir` is parsed by the same `MirParser` and merged into the
  output module at link time. This avoids ever needing to express
  inline 6502 in the MIR dialect.

The actual helper bodies do the same OSWRCH / OSWORD work the existing
`M6502CodeGenerator` already emits. They use exclusively ops covered
by this plan (`mem.load`, `mem.store`, `call.func @__OSWRCH`,
`cf.cond_br`, …); the `__OSWRCH` / `__OSWORD` thunks are themselves
hand-written `mos6502.*` MIR (`pseudo.copy → $a`, then `mos6502.jsr.abs
$FFEE`, …) — the only place raw absolute-address `jsr` is allowed.

---

## 5. Machine-code emit table extensions

Today [`MOS6502MachineCodeEmitTable`](../src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs)
has 7 rules: `Clc, AdcZp, StaZp, LdaZp, LdxZp, Txa, Rts`. The full
test set needs (in roughly the order they come into play):

```
SbcZp  SbcImm                              — subtract chain
CmpZp  CmpImm  Sec                         — comparison setup
Beq  Bne  Bcs  Bcc  Bmi  Bpl  JmpAbs       — branches + unconditional jump
LdaImm  LdxImm  LdyImm                     — constants → A/X/Y
StaIndY  LdaIndY  LdyImm                   — indirect-Y load/store for mem ops
JsrAbs                                     — direct calls
JmpInd                                     — virtual dispatch (later)
StaZpX  LdaZpX (?)                         — only if AMS chooses these
Pha  Pla                                   — only if frame ops need them
```

Each row is one line. The negative test in
[`Mir/MachineCodeEmitterTests.cs`](../src/Irie.Tests/Mir/MachineCodeEmitterTests.cs)
already catches unmapped opcodes — extend its allowlist as each row
lands.

Two new `EmitOperandKind`s are needed for the symbol-half lda variants
(see §4.4): `SymbolLowByte` and `SymbolHighByte`. Both read a generic
`Symbol` operand off the MIR instruction and emit a
`MachineCodeOperand.ExternalRef` tagged with the half. No change to
MIR's generic `MirOperand` types — the half-tag travels through the
emit-table rule, not through a new operand variant.

---

## 6. Stepwise landing order

Each step compiles and tests pass before the next. Each step is one PR.
The order is chosen so that *each step ends with a passing
end-to-end test* — never a half-built feature waiting for the next PR.

The driving end-to-end target at each step is the simplest test that
becomes runnable. Tests that need ops not yet covered stay
unimplemented (commented out as today's `[CompilerTest]`s already are
for unsupported cases) — they're added as the relevant slice lands.

1. **`arith.subi_with_borrow` + multi-byte subtract.** Mirror the
   `addi_with_carry` work end-to-end (legalize → isel → AMS → expand →
   emit). Add lit test `IntegerSub32-*.irie` matching today's
   `IntegerAdd32-*.irie` chain. No new dialects.

2. **`arith.cmpi` predicate encoding + i32 → i8 narrowing.** Land the
   predicate-immediate format (resolves [unified-ir-plan #1](unified-ir-plan.md)).
   No isel changes yet — lit test stops after Legalizer.

3. **Cmp + cond_br fusion.** Wire the isel fusion path: `cmpi i8` +
   `cf.cond_br` → `mos6502.cmp` + `mos6502.b<pred>` + `mos6502.jmp.abs`
   (with `bgt`/`blt`/`bge` synthesised). Lit test for an i8 less-than
   branch. Re-test `IntegerSub32` to confirm cmp doesn't conflict with
   sub on the SBC chain.

4. **`call.func` + `CallLowering.LowerCall`.** Add the op, the lowering,
   the emit row for `JsrAbs`, and a minimal lit test calling a sibling
   function. *No memory dialect yet — the call returns and takes only
   primitive args.* This unblocks `CallMethodWithReturnValue`,
   `CallMethodWithParameterAndReturnValue`,
   `CallMethodWithTwoParametersAndReturnValue`,
   `CallNestedMethods` once a frontend exists. Pure-MIR test stays at
   one calling fixture.

5. **`call.func` clobber barriers in RA.** Verify that implicit-defs on
   `jsr.abs` shorten live ranges. Add a lit test where a vreg live
   across a call is correctly spilled or reloaded. (Spilling is not
   implemented yet — this test deliberately picks a case the
   allocator can handle without spilling, e.g. ≤ 2 i8 live values.)

6. **`pseudo.extract` + `pseudo.insert` + artifact-combiner rules.**
   Pre-req for §4.4's symbol-half handling. Lit test: artifact-combine
   `pseudo.extract` of `pseudo.merge` to a direct vreg reference.

7. **`MirGlobal` + initializers + `mem.symbol`.** Add the
   `MirModule.Globals` list, the `MirInitializer` / `MirDataItem` types,
   text + binary persistence, and the `mem.symbol` MIR op. Lit test
   round-trips a global with a `DataBytes` initializer and uses it as
   the source of a `mem.symbol`.

8. **`mem` dialect: `mem.load`, `mem.store` (i8).** Lit test that loads
   / stores one byte via a `mem.symbol`-produced address. Adds the
   `LdaIndY` / `StaIndY` / `LdyImm` emit rows plus the
   `LdaImmSymLo` / `LdaImmSymHi` rows (driven by the new
   `pseudo.extract` rule from step 6).

9. **`mem.load/store` of i16 / i32.** Narrowing in Legalizer. Re-uses
   step-8 rules per byte.

10. **`cast` dialect (`cast.trunc` only).** Plus its legalization to
    `pseudo.unmerge` + byte-vreg pick. Lit test that truncates i32 →
    i16 and uses the result in a `mem.store`. (Other cast ops added on
    demand.)

11. **`mem.fill` + `MirFunction.FrameSlots` + `FrameLoweringPass`.**
    Unlocks `UseStruct`. Frontend emits one frame slot per
    address-taken local; `FrameLoweringPass` materialises
    `mem.symbol @<func>_local<N>` and registers a `MirGlobal` (no
    initializer = zero-init / `.bss`). Lit test exercises a struct
    stored into a frame slot and read back.

12. **String literals via globals.** No new MIR op — `ldstr` in the
    frontend allocates a `MirGlobal` with a `DataBytes` initializer
    and lowers to `mem.symbol @str_<hash>`. Unlocks `PrintHelloWorld`.

13. **Hand-written `runtime.mir` BCL helpers.** Port the existing
    `CompileSystemConsoleBeep` / `WriteLineInt32` / `WriteLineString` /
    `ReadLine` from `M6502CodeGenerator` into MIR. They reference
    `mos6502.jsr.abs` with absolute literal addresses for OS calls
    (`$FFEE` etc.) — adds an `Immediate` form of `JsrAbs`. Adds the
    `Print42`, `PrintMinus1`, `PrintHelloWorld`, `AddInt32` test
    targets to scope.

14. **Static field reads/writes.** Static fields are just `MirGlobal`s
    with no initializer (zero-init). `stsfld` / `ldsfld` lower to
    `mem.store` / `mem.load` against `mem.symbol`. No new MIR.
    Unlocks `UseStaticFields`.

15. **`for` loop test.** Cmpi i32 + bge fusion in a real loop. Mostly
    pre-existing, but verifies the post-RA branch ranges are short
    enough for 6502 8-bit relative encoding (or that AMS rewrites a
    too-far `BNE target` into `BEQ skip; JMP target`). First-cut:
    short branches only; document the relative-range limitation.

16. **Vtables as globals.** Vtables are `MirGlobal`s whose initializer
    is an array of `DataSymbolRef` items pointing to the slot methods.
    The frontend builds them from its existing `VtableTracker`. No new
    MIR type, no new dialect — it's just data.

17. **`newobj` + `ManagedHeap.Alloc` runtime call.** Class instance
    allocation. Frontend lowers to: load size as i16 const, load
    vtable address as `mem.symbol @Type_vtable`,
    `call.func @ManagedHeap_Alloc`. Unlocks `UseClass`,
    `UseNestedClass`, `UseNestedClassWithConstructors`,
    `UseStructWithConstructor`.

18. **`call.indirect` + virtual dispatch in the frontend.** Add the MIR
    op + AbiLowering rule + isel + the `runtime.mir`
    `__call_indirect_trampoline`. Frontend lowers IL `callvirt` to
    `mem.load`s of the vtable slot followed by `call.indirect` (see
    §4.8 — MIR sees no vtable). Unlocks
    `CallOverriddenMethodOnClass`.

19. **Instance methods (`call.func` against a method whose first
    parameter is `this`).** No new MIR; just frontend wiring. Unlocks
    `CallInstanceMethodOnStruct`, `CallInstanceMethodOnClass`.

20. **Non-reentrance guard in the frontend.** Tarjan SCC over the
    method-call graph; throw a clear `NotImplementedException` when a
    cycle is detected. Add a unit test that constructs a recursive
    method set and asserts the throw.

21. **`ReadAndWriteLine`.** Last test. Needs string buffer allocation
    in a frame slot, `OSWORD 0` runtime call, and `Console.Beep`.
    Mostly drives the existing slices; verify the lit test passes.

22. **Switch `DotNetro.Compiler` over to Irie.** Out of scope of this
    plan but stated as the goal — listed for orientation. Done as a
    separate sequence once steps 1–21 are landed.

---

## 7. Open questions / deferred work

1. **Resolved — Symbol-half addressing via `pseudo.extract`.** The
   `lda.imm #<sym` / `#>sym` need is handled by `pseudo.extract` of a
   `mem.symbol`-defined i16 vreg, pattern-matched at isel into
   `mos6502.lda.imm.symlo @sym` / `mos6502.lda.imm.symhi @sym`. The
   target instructions carry a `Symbol` operand directly; no
   `SymbolHalf` variant on the generic `Immediate` is needed. Same
   approach scales to any future op that needs to address part of a
   symbol address. See §2.4 and §4.4.

2. **Resolved — `call.func` lowers to `mos6502.jsr.abs`** (target
   dialect). AbiLowering is allowed to emit target-dialect ops
   directly; it already does via `pseudo.copy`. No pass-framework
   change needed.

3. **Spilling.** Out of scope for this plan, but every step here is
   sized to avoid spilling (≤ ~8 live i8 values at once given the
   16-physreg CC_MOS budget). The first test that *needs* spilling
   (probably `ForLoop` plus extra locals, or `CallNestedMethods` at
   depth ≥ 2) will trigger an explicit `NotSupportedException` in the
   allocator; spilling lands as a separate workstream after this
   plan's switchover (step 19).

4. **Branch range.** 6502 conditional branches are signed 8-bit
   relative. Any branch whose distance exceeds ±127 bytes needs the
   inverted-branch-around-jmp idiom (e.g. `BNE +3 ; JMP target`). For
   first cut, every conditional branch is emitted directly and we
   pessimistically assume no test produces one out of range. Verify
   when `ForLoop` lands; add the rewriter pass if needed (small,
   post-emit, MachineCode-level).

5. **`Dup` semantics.** In MIR/SSA, `Dup` is a no-op (the same vreg is
   referenced twice). The frontend's IL→MIR layer collapses it. No
   MIR op needed.

6. **`Initobj` size mismatches.** IL `initobj <Type>` is "zero the
   region pointed at by the address on top of stack". `mem.fill` with
   `byte = 0` and `count = Type.Size` handles this directly.

7. **`cast.zext` / `cast.sext` legalization shape.** Initial plan
   (§2.5) lowers via `pseudo.merge` of source-byte vregs plus
   constant / sign-replicated upper bytes. The sign-replication for
   `sext` needs an `arith.asr` (or equivalent) to broadcast the top
   bit of the high byte — not currently in the arith dialect. Add
   when the first test needs `sext`; not needed for the active tests.

---

## 8. File-by-file delete / create / edit list

Compiled summary of which files this plan touches. Listed for
landing-step traceability; each step in §6 will edit a strict subset.

### Create

```
src/Irie/Dialects/Mem/MemDialect.cs
src/Irie/Dialects/Mem/MemOp.cs
src/Irie/Dialects/Call/CallDialect.cs
src/Irie/Dialects/Call/CallOp.cs
src/Irie/Dialects/Cast/CastDialect.cs
src/Irie/Dialects/Cast/CastOp.cs
src/Irie/Mir/MirGlobal.cs
src/Irie/Mir/MirInitializer.cs        ; + MirDataItem variants
src/Irie/Mir/FrameSlot.cs
src/Irie/Passes/FrameLoweringPass.cs
src/Irie/Target/MOS6502/MOS6502MemLegalizer.cs    ; or inline into MOS6502LegalizerInfo
src/DotNetro.Compiler/Irie/                       ; new dir for frontend bridge
  IrFrontendDriver.cs
  IlToMirCompiler.cs
  NonReentranceAnalyzer.cs
src/DotNetro/Runtime/runtime.mir
src/Irie.Tests/Lit/CodeGen/MOS6502/IntegerSub32-*.irie
src/Irie.Tests/Lit/CodeGen/MOS6502/CmpI32Branch-*.irie
src/Irie.Tests/Lit/CodeGen/MOS6502/CallFunc-*.irie
src/Irie.Tests/Lit/CodeGen/MOS6502/MemLoadStore-*.irie
src/Irie.Tests/Lit/CodeGen/MOS6502/FrameSlot-*.irie
```

### Edit

```
src/Irie/Dialects/Arith/ArithOp.cs         ; + SubICarry, predicate on CmpI
src/Irie/Dialects/Arith/ArithDialect.cs    ; name + parse
src/Irie/Dialects/Pseudo/PseudoOp.cs       ; + Extract, Insert
src/Irie/Dialects/Pseudo/PseudoDialect.cs  ; name + parse for Extract/Insert
src/Irie/Mir/MirModule.cs                  ; + Globals
src/Irie/Mir/MirFunction.cs                ; + FrameSlots
src/Irie/Mir/Parsing/MirParser.cs          ; new module-level decls
src/Irie/Mir/Writing/MirWriter.cs          ; new module-level decls + frame slot defs
src/Irie/Mir/Binary/MirBinary*.cs          ; tagged section for globals (with initializers)
src/Irie/Passes/AbiLoweringPass.cs         ; calls CallLowering.LowerCall for call.func / call.indirect ops
src/Irie/Passes/LegalizerPass.cs           ; recognise cast.* and extend artifact combiner for pseudo.extract/insert
src/Irie/Target/CallLowering.cs            ; + abstract LowerCall
src/Irie/Target/MOS6502/MOS6502CallLowering.cs   ; + LowerCall impl
src/Irie/Target/MOS6502/MOS6502LegalizerInfo.cs    ; rules for SubI, CmpI, mem.*, cast.*
src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs   ; SubI, CmpI fusion, mem.*, call.*, pseudo.extract of mem.symbol → LdaImmSymLo/Hi
src/Irie/Target/MOS6502/MOS6502AddressingModeSelectorPass.cs ; Sbc, Cmp refinements
src/Irie/Target/MOS6502/MOS6502Op.cs       ; + LdaImmSymLo, LdaImmSymHi
src/Irie/Target/MOS6502/MOS6502Dialect.cs  ; + name/parse for new ops
src/Irie/Target/MOS6502/MOS6502PseudoExpander.cs   ; zp dest immediate loads
src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs ; rows per §5
src/Irie/Target/MOS6502/MOS6502MachineCodeEmitter.cs ; SymbolLowByte / SymbolHighByte emit kinds
src/Irie.Tools.Compiler/Program.cs          ; expose `--emit=ssd` (BBC Micro image)?
src/DotNetro.Compiler/CompilerDriver.cs    ; eventually: switch to Irie
src/DotNetro.Compiler/DotNetCompiler.cs    ; eventually: IL → MIR instead of asm
CLAUDE.md                                  ; document new dialects + module sections
notes/unified-ir-plan.md                   ; resolve open question #1 (cmpi predicate)
```

### Delete (eventually, step 19+)

```
src/DotNetro.Compiler/CodeGen/M6502CodeGenerator.cs
src/DotNetro.Compiler/CodeGen/BbcMicroCodeGenerator.cs
src/DotNetro.Compiler/CodeGen/CodeGenerator.cs       ; base class
```

---

## 9. Verification ladder

For each step in §6 the verification target is one of:

- **Lit test (`.irie`)** — parses fixed MIR text, runs through the
  pipeline to the stop-after point, CHECK-matches the output. The
  format used by the existing `Irie.Tests/Lit/CodeGen/MOS6502/` tests.
- **Unit test (`Irie.Tests/...Tests.cs`)** — constructs a tiny MIR
  module programmatically, runs a single pass, asserts the resulting
  structure. Used for non-textual invariants (e.g. clobber barriers in
  RA).
- **DotNetro compiler test (`DotNetro.Compiler.Tests`)** — once the
  frontend bridge exists at step 4+ and we have an end-to-end path,
  one of the `[CompilerTest]` methods becomes runnable through Irie.
  Per CLAUDE.md, these run the compiled 6502 in the Aemula emulator
  and diff against reference .NET output.

The compiler tests are the final acceptance criterion: when every
`[CompilerTest]` in `DotNetCompilerTests` passes through the Irie
backend, this plan is complete and step 19 (the backend swap) can
land.
