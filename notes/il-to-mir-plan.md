# IL → MIR Translator — Implementation Plan

Drafted 2026-06-03. Transitions `DotNetro.Compiler` from straight-to-6502
codegen to translating IL → Irie MIR, then reusing the Irie pipeline
(AbiLowering → Legalizer → InstructionSelector → … → BinaryEncoder → PackageImage)
as-is.

---

## STATUS — last updated 2026-06-09 (branch `irie-close-gap`)

**To resume: this is a partially-executed plan. Steps 1–8 are done; step 9 is
~71% done (15 of 21 lit tests ported); step 10 is not started. Read this
section, then continue at "What's left" below.**

### Done

- **Steps 1–8 (gaps G1–G5, runtime skeleton, translator skeleton, smoke
  tests): complete.** All five Irie gaps closed, `runtime.irie` exists with
  OS-call wrappers + `WriteLineInt32/String/Boolean` + `ReadLine`/`Beep`,
  `IlToMirTranslator` + `--mir` flag landed on `CompilerDriver.Compile`.
- **Step 9 — TRACTABLE groups: done.** 12 lit tests ported to
  `src/DotNetro.Compiler.Tests/Lit/Mir/<name>.cs` (copies whose RUN line adds
  `--mir`); legacy `Lit/*.cs` left untouched. Ported & green end-to-end:
  AddInt32, Print42, PrintMinus1, CallMethodWith{Parameter,
  ParameterAndReturnValue, ReturnValue, TwoParametersAndReturnValue},
  HelloWorld, UseStaticFields, ForLoop, CompareLessThanInt32, ReadAndWriteLine.
  Translator gained: BB discovery + liveness-based block params, branches,
  string/static-field globals, bare-cmpi materialization via a cond_br diamond.
  isel gained signed multi-byte compare (slt/sge via sign-flip→unsigned
  ladder); encoder gained branch relaxation (Bxx_inverse;JMP); legalizer
  narrows wide block params.
- **Detour — register-allocator redesign: complete (Phases 0–5).** The old
  block-local RA couldn't handle the control-flow / cross-call pressure the
  deferred test groups create, so we executed
  [register-allocator-redesign-plan.md](register-allocator-redesign-plan.md):
  physreg-aware `LiveIntervals` → graph-colouring RA → coalescer → spilling +
  rematerialization (abstract frame slots) → register-preference cost tuning,
  plus class-constraint gap closers (commits `0529e22`…`f0d566d`). Phase 6
  (live-range splitting) is optional and was skipped.

### What's left

**Step 9 — DEFERRED groups (6 tests still legacy-only).** Each remaining test
is blocked on a backend capability; tackle the blockers in roughly this order
(earlier ones unblock more tests):

| Blocker | Gates |
|---|---|
| ~~**Runtime-pointer-load isel**~~ — **DONE (2026-06-09).** `mem.load.byte_at` / `mem.store.byte_at` now lower runtime i16 pointers (address vreg defined by a 2-byte `pseudo.merge`) by copying the byte vregs straight into the `$zp0/$zp1` indirect-Y scratch pair, alongside the existing `mem.symbol` path. `MOS6502InstructionSelector.EmitPointerSetup` dispatches on `TryResolveSymbolAddress`; cache key generalised from symbol name to `@name`/`%vreg`. Also fixed a latent RA/expander correctness bug this exposed: a `$x↔$y` (or `zp↔zp`) `pseudo.copy` clobbers `$a` as hidden scratch, corrupting a low pointer byte left live in `$a` across it. Rather than special-case it, **unified all copy-scratch handling into the post-RA `RegisterScavengingPass`**: `MOS6502PseudoExpander` now re-emits `$x↔$y` / `zp→zp` copies as scratch forms (like immediate→zp), and the scavenger picks a location dead at the copy's point — for `$x↔$y` a dead zp bounce slot (`STX $tmp; LDY $tmp`, touching no GPR), for `zp→zp`/imm→zp a dead GPR. `MOS6502ParallelCopyPass` shrank to pure parallel-copy sequentialization (all `$a`-evacuation logic deleted). Per-shape candidates via `TargetRegisterInfo.GetScratchCandidates`. Lit tests: `RuntimePointerLoad-InstructionSelector.irie`, `RuntimePointerLoadStore-MachineCode.irie`. | fields, structs, callvirt |
| ~~**FrameSlot locals**~~ — **DONE (2026-06-10).** `IlToMirTranslator` classifies each local upfront (`ScanAddressTakenLocals` + value-type check): value-type/`ldloca`-taken locals become `FrameSlot`s (slot type a wide `IntegerType(size*8)` so the existing `FrameLoweringPass` sizes the `.bss` global correctly), everything else stays SSA. New IL handlers: `ldloca` (push `mem.frame_addr`), `ldfld`/`stfld` (`EmitFieldAddress` = base `+offset` via `arith.addi i16`, then `mem.load/store.iN`), `initobj` (`mem.fill 0`), and struct `ldloc`/`stloc` (push slot address / byte-wise `EmitStructCopy`). FrameSlot locals are excluded from the SSA zero-init seeding and block-parameter threading. Also fixed a latent **RA coalescer miscompile** this exposed: a byte-copy `mem.load.i8 @symA` → `mem.store.i8 @symB` left the `lda.indy` result (`ac`-pinned) coalesced onto `$x` because `GraphColouringAllocator.Combine` kept the broad node's allowed-colour set instead of intersecting with the `ac` end's `{$a}` — `LDA ($zp),Y` then emitted while MIR claimed `$x`. Fix: `Combine` now intersects `_allowedColours`, and `CoalesceIsSafe`'s Briggs test uses the intersection size (rejecting empty-intersection merges). Lit tests: `Lit/Mir/{UseStruct,UseStructWithConstructor,CallInstanceMethodOnStruct}.cs`. | UseStruct, UseStructWithConstructor, CallInstanceMethodOnStruct |
| **Cross-call value preservation** — locals live across a call get clobbered (`CallerSavedScratch` covers RC2..RC15, callees reuse RC16+). Separate workstream: [static-stack-alloc-plan.md](static-stack-alloc-plan.md) | CallNestedMethods (HELD), and all heap/class groups |
| **ManagedHeap_Alloc + vtables + callvirt / indirect dispatch** | UseClass, UseNestedClass, UseNestedClassWithConstructors, CallInstanceMethodOnClass, CallOverriddenMethodOnClass |

The 6 deferred tests: CallNestedMethods, UseClass, UseNestedClass,
UseNestedClassWithConstructors, CallInstanceMethodOnClass,
CallOverriddenMethodOnClass. Port each to `Lit/Mir/` (add `--mir` to the RUN
line) and verify the full suite stays green (Debug + Release).

**Step 10 — flip the default + delete the old path.** Only after all 21 lit
tests are green on `--mir`: make `--mir` the default in `CompilerDriver.Compile`,
then delete `src/DotNetro.Compiler/CodeGen/` and the eval-stack machinery
(`_stack`, `PushStackEntry`, `PopStackEntry`, per-op `Write*` calls) in
`DotNetCompiler`. `EcmaMethod.FrameSize` / `ParametersSize` likely go too.

The numbered "Migration steps" and op-by-op tables below are the original
reference; they remain accurate for the work that's left.

---

## Goal

Replace the [CodeGenerator](../src/DotNetro.Compiler/CodeGen/CodeGenerator.cs)
hierarchy with a new `IlToMirTranslator` that walks the same IL the existing
[DotNetCompiler.cs](../src/DotNetro.Compiler/DotNetCompiler.cs) does, and
produces a `MirModule` consumed by the same pass pipeline that
[Irie.Tools.Compiler](../src/Irie.Tools.Compiler/Program.cs) drives. The
output of `CompilerDriver.Compile` for `mos6502-bbcmicro` should be byte-for-byte
*replaceable* (not necessarily identical) — same `.ssd`, same behavior.

## Approach

Side-by-side. Add `IlToMirTranslator` next to `DotNetCompiler`; gate via a
flag on `CompilerDriver.Compile`. Port lit tests one at a time. Once all green,
flip the default, delete the old `CodeGen/` directory and all the
`Stack<TypeDescription>` / soft-stack machinery in DotNetCompiler.

The translator's outer loop mirrors `DotNetCompiler.CompileMethodBody` — a
switch on IL opcode — but each handler calls `MirBuilder` instead of
`CodeGenerator`. The eval stack becomes `Stack<int>` (vreg IDs) running
alongside the existing `Stack<TypeDescription>`.

## IL → MIR op-by-op

These are the IL ops the current `DotNetCompiler` handles
([DotNetCompiler.cs:181-358](../src/DotNetro.Compiler/DotNetCompiler.cs#L181-L358)).
Lowering:

| IL | MIR |
|---|---|
| `ldarg.N` | push parameter vreg N (entry-block parameter); for address-taken params, push a `mem.load.iN @<param_slot>` |
| `ldloc.N` | push the local's current SSA name; for FrameSlot locals, push `mem.load.iN @<local_slot>` |
| `stloc.N` | overwrite the local's SSA name; for FrameSlot locals, `mem.store.iN @<slot>, %v` |
| `ldloca.N` | force local to a FrameSlot, push `mem.frame_addr <slot>` |
| `ldc.i4.{0..5,m1,s,full}` | `%v : i32 = arith.constant <value>` |
| `add` (i32) | `arith.addi %a, %b : i32` |
| `add` (intptr) | `arith.addi %a, %b : i16` |
| `conv.i` (i32→intptr) | `cast.trunc %x : i16 ← i32` |
| `clt` | `arith.cmpi <slt>, %a, %b` → i1; push i1 |
| `cge` (used by Bge) | `arith.cmpi <sge>` |
| `brfalse` | `arith.cmpi <eq>` against `arith.constant 0` + `cf.cond_br` |
| `brtrue` | `arith.cmpi <ne>` against `arith.constant 0` + `cf.cond_br` |
| `blt.s` / `bge.s` | `arith.cmpi <slt/sge>` + `cf.cond_br` (Irie's isel fuses these) |
| `br.s` | `cf.br bbN` |
| `ldfld` | `arith.addi %obj_ptr, <field.Offset>` + `mem.load.iN`; for value-type-on-stack: address-take via a temp FrameSlot first |
| `stfld` | symmetric: `arith.addi` + `mem.store.iN` |
| `stind.i` | `mem.store.i16 %ptr, %val` |
| `initobj` | `mem.fill %ptr, <0:i8>, <size>` |
| `dup` | re-push the same vreg ID |
| `ldstr` | register string as a `MirGlobal`, push `mem.symbol @<key>` |
| `ldsfld` | `mem.load.iN @<static_field_symbol>` |
| `stsfld` | `mem.store.iN @<static_field_symbol>, %v` |
| `sizeof` | `arith.constant <type.Size> : i32` |
| `newobj` | `call.func @ManagedHeap_Alloc(size, vtablePtr) → %p`; `call.func @ctor(%p, args…)`; push `%p` |
| `call` | `call.func @callee(args…) → returns` |
| `callvirt` | vtable lookup (`mem.load.i16` of `this-2`, then `mem.load.i16` of `vtable+slot*2`); `call.indirect %fptr, %this, args…` |
| `ret` | `core.return [%v]` |
| `nop` | nothing |

## Basic block discovery

IL has no BBs. Pre-scan the IL once to find all branch targets and the
instructions immediately following branches; those addresses start new blocks.
Emit a `MirBlock` for each block. The eval-stack `Stack<int>` exists per-block;
at block boundaries the stack must either be empty (the common case for the
existing test corpus) or be materialized as block parameters on the
successor. Initial implementation: assert empty-at-boundaries; relax later
if a test needs it.

## Locals strategy

Two flavors:

- **Primitive locals (i32/i16/i8)** that are never address-taken — pure SSA
  vregs. Each `stloc` overwrites the current SSA name; each `ldloc` reads it.
  The register allocator handles them.
- **Value-type locals OR address-taken locals** — `FrameSlot` per local;
  `FrameLoweringPass` materializes them as zero-init `MirGlobal`s. Every
  `ldloc` becomes `mem.load.iN`; every `stloc` becomes `mem.store.iN`.

Decision is made upfront per local by scanning for `ldloca`. If present, the
local is FrameSlot-bound; otherwise SSA.

## Parameters strategy

Same split. Default to entry-block parameters (vreg-based, lowered by
AbiLoweringPass into pseudo.copy-from-physreg). For address-taken parameters
(`ldarga` if/when supported), force into a FrameSlot at function entry and
`mem.store` the live-in vreg into the slot.

## Strings, static fields, vtables

All become `MirGlobal` entries on the module:

- **String constants** — `MirGlobal(name, ?, byteCount, MirInitializer(DataBytes(ascii ++ \r ++ 0)))`.
  Mirror the existing `.cstring "...", 13` format (CR before null).
- **Static fields** — `MirGlobal(<owner>_<field>, fieldType, fieldSize, null)` (bss).
- **Vtables** — `MirGlobal(Vtable_<type>, ?, slots*2, MirInitializer([DataSymbolRef(methodName), …]))`.
  Plain function pointers, no `-1` offset (the old RTS-trick goes away; the
  indirect-call trampoline does `JMP (zp)`).

## Calling sequence

- **Direct call** → `call.func @callee(args…) → returns`. AbiLoweringPass +
  MOS6502CallLowering already turn this into `pseudo.copy → physreg` sequences
  + `mos6502.jsr.abs`.
- **Newobj** → two calls:
  1. `call.func @ManagedHeap_Alloc(<size>, <vtablePtr>) → %ptr`
  2. `call.func @<ctor>(%ptr, args…)`
- **Callvirt** →
  1. `%vtable_ptr : i16 = mem.load.i16 (this - 2)` (small `arith.subi` first)
  2. `%fptr : i16 = mem.load.i16 (vtable_ptr + slot_index*2)`
  3. `call.indirect %fptr, %this, args… → returns`

## BBC Micro OS calls

Wrapper functions `oswrch`, `osasci`, `osword` defined in `runtime.irie` whose
bodies are `mos6502.jsr.abs Immediate($FFEE)` etc. + `mos6502.rts`. Frontends
emit `call.func @oswrch` like any other call. Costs +3 cycles per OS call;
trivial.

## Entry point

`start` is a hand-written MIR function in the runtime, placed first in
`module.Functions` so it lays out at `origin`. Body:

1. (BBC Micro only) `JSR oswrch` for `LDA #22; LDA #7` — MODE 7 setup.
2. `call.func` for each static constructor (translator builds this list).
3. `call.func @Main_<sig>`.
4. `mos6502.rts`.

The "for each static constructor" list is generated by the translator. Two
ways to wire it in:

- Insert a placeholder block in the hand-written `start` and have the
  translator post-process it (scan for a `mos6502.nop` with `Symbol("__staticctors__")` or similar).
- Or: hand-write `start` *without* static-ctor calls and have the translator
  add a new block that wraps it: `__entry`: call all static ctors, then
  `jmp start`. Slightly cleaner.

Prefer the second.

## Runtime file layout

`src/Irie/Target/MOS6502/runtime.irie` — the canonical hand-written runtime
for the MOS6502 target. Loaded by `IlToMirTranslator` as an embedded resource
on `MOS6502Target`. The BBC Micro variant inherits the same runtime today
(MODE 7 lives in `start`, which the translator can specialize per-target via
a `Target.GetRuntime()` hook returning different `.irie` content; for now,
single file with `start` containing the MODE 7 init).

Contents:
- `start` — entry point
- `__call_indirect_trampoline` — `JMP ($1E)` (using the RC30/RC31 zero-page
  slots that `MOS6502CallLowering.LowerIndirectCall` parks the target pointer in)
- `oswrch`, `osasci`, `osword` — `JSR $FFEE` etc. wrappers
- `ManagedHeap_Alloc` — translated from the C# version, OR hand-written; the
  C# implementation lives in [ManagedHeap.cs](../src/DotNetro/Runtime/ManagedHeap.cs)
- `System_Console_Beep`, `System_Console_ReadLine`, `System_Console_WriteLine_Int32`,
  `System_Console_WriteLine_String` — hand-ported from
  [BbcMicroCodeGenerator.cs](../src/DotNetro.Compiler/CodeGen/BbcMicroCodeGenerator.cs)

The `WriteLineInt32` pretty-printer is heavy 6502; it should be written
directly against the `mos6502.*` ops (skipping the generic dialects). The
pipeline passes it through (isel sees already-selected ops; pseudo-expansion
ignores them).

## Module assembly

`IlToMirTranslator` produces a `MirModule` like this:

```
1. Parse runtime.irie → MirModule
2. Translator inserts/specializes `start` (static-ctor list)
3. BFS-walk methods from entry point; for each non-runtime method:
     a. Emit a MirFunction (with entry-block parameters and FrameSlots)
     b. Walk IL, emit MIR per the table above
4. Append all collected strings/static fields/vtables as MirGlobals
5. Hand the module to the pass pipeline (FrameLowering → AbiLowering → …)
6. Pipeline emits MachineCode → encoder produces bytes → PackageImage to .ssd
```

## Gaps in Irie that block the translator

These must be filled before `IlToMirTranslator` can compile even a trivial
program. Each is small enough to be its own PR:

### G1: `MOS6502BinaryEncoder` doesn't lay out globals

[MOS6502BinaryEncoder.cs:182-212](../src/Irie/Target/MOS6502/MOS6502BinaryEncoder.cs#L182-L212)
walks `module.Functions` only. `module.Globals` is never consulted. Without
this fix, strings/static-fields/vtables won't make it into the binary.

Fix: after laying out functions, walk `module.Globals`, assign each one an
address at the running cursor, record it in `_functionAddrs` (or a sibling
map shared with function-symbol resolution), and in pass 2 write
`DataBytes.Bytes` raw and `DataSymbolRef` as 2-byte LE references resolved
against the symbol table. Add lit tests in `Irie.Tests` covering globals
emission.

### G2: `__call_indirect_trampoline` doesn't exist yet

[MOS6502CallLowering.cs:188-193](../src/Irie/Target/MOS6502/MOS6502CallLowering.cs#L188-L193)
references `@__call_indirect_trampoline` but the comment notes it "lives in
runtime.mir (hand-written; written in a later step)". Without it, callvirt
won't link.

Body: copy `RC30/RC31` (the parked target address) into zero-page (already
there, since RC30/RC31 ARE zero-page slots), then `JMP ($1E)`. Note the
hardcoded `$1E` in the comment may need updating to match RC30's actual
zero-page byte — verify before writing.

### G3: `arith.constant` for i16 / i32

[MOS6502LegalizerInfo.cs:34-35](../src/Irie/Target/MOS6502/MOS6502LegalizerInfo.cs#L34-L35)
only lists `Constant when i1` and `Constant when i8` as Legal. `ldc.i4 42`
needs `arith.constant 42 : i32`.

Fix options:
- (a) Legalizer narrows `arith.constant : i32` to four `arith.constant : i8`
  + `pseudo.merge`. Mirrors the addi narrowing path.
- (b) Isel handles wide constants by emitting `lda.imm` per byte + merging.

Option (a) is more uniform with the rest of the legalizer. Recommend (a).

### G4: `arith.cmpi` at i16 / i32

The legalizer says cmpi is always Legal (it dispatches on the i1 def type),
but `GetCmpINarrowType` referenced in the comment isn't implemented and the
isel's `SelectCmpI` may not handle multi-byte operands. Verify by writing
a `cmpi i32` lit test in `Irie.Tests`; if it fails, add legalizer narrowing
that produces a chain of per-byte compares ending in a single i1.

### G5: `arith.cmpi <eq>` / `<ne>` predicates and zero-compare fusion

For `brfalse`/`brtrue`. Predicates exist in `ArithCmpPredicate` (verify enum)
and the cmpi+cond_br fusion path should already cover it. Add lit tests in
`Irie.Tests` to confirm.

## Migration steps

1. **G1** — extend `MOS6502BinaryEncoder` to lay out `MirGlobal`s. PR + lit tests.
2. **G2** — write `__call_indirect_trampoline` in MIR as a tiny standalone
   `runtime.irie` skeleton; verify a hand-rolled call.indirect lit test works.
3. **G3** — narrow `arith.constant : i32` in the legalizer.
4. **G4** — narrow `arith.cmpi i32` if needed.
5. **G5** — cmpi-eq/ne lit tests.
6. **Runtime skeleton** — add `oswrch`/`osasci`/`osword` wrappers + `start`
   (without static-ctor logic yet) + the Console helpers + `ManagedHeap_Alloc`
   to `runtime.irie`. Load it via embedded resource in `MOS6502Target`.
7. **Translator skeleton** — `IlToMirTranslator` class in
   `src/DotNetro.Compiler/`; copy the IL-walking structure from
   `DotNetCompiler` but call `MirBuilder` per op. Behind a flag on
   `CompilerDriver.Compile`.
8. **Smallest lit test first** — `Print42.cs` or `AddInt32.cs`. End-to-end
   through the new pipeline.
9. **Port tests in this order**: arithmetic → control flow (if/while/for) →
   fields → calls → static fields → newobj/heap → callvirt → strings.
10. When all lit tests pass on the new flag, flip the default. Delete
    `src/DotNetro.Compiler/CodeGen/`, the eval-stack machinery
    (`_stack`, `PushStackEntry`, `PopStackEntry`), and the per-op
    `Write*` calls in `DotNetCompiler`. `EcmaMethod.FrameSize` / `ParametersSize`
    likely go too (the new pipeline doesn't have a frame in that sense).

## Files added

- `src/Irie/Target/MOS6502/runtime.irie` (embedded resource)
- `src/DotNetro.Compiler/IlToMirTranslator.cs`
- Lit tests for the gaps (`G1`..`G5`) in `src/Irie.Tests/`

## Files modified

- `src/Irie/Target/MOS6502/MOS6502BinaryEncoder.cs` — global layout (G1)
- `src/Irie/Target/MOS6502/MOS6502LegalizerInfo.cs` — wide constants / cmpi (G3, G4)
- `src/Irie/Target/MOS6502/MOS6502Target.cs` — expose runtime resource
- `src/DotNetro.Compiler/CompilerDriver.cs` — flag selecting old vs new pipeline
- `src/DotNetro.Compiler.Driver/Program.cs` — surface the flag if exposed via CLI

## Files removed (after step 10)

- `src/DotNetro.Compiler/CodeGen/CodeGenerator.cs`
- `src/DotNetro.Compiler/CodeGen/M6502CodeGenerator.cs`
- `src/DotNetro.Compiler/CodeGen/BbcMicroCodeGenerator.cs`
- Soft-stack / eval-stack machinery in `DotNetCompiler` (or DotNetCompiler
  itself if the translator subsumes it)

## Open questions

- Does the indirect-call trampoline's `JMP ($1E)` hardcode match the actual
  byte address of RC30 in zero page? (Verify before writing.)
- Should `Driver.Program.cs` expose the pipeline flag (`--mir`?), or is the
  flag internal to tests and gets removed before release?
- Is `ManagedHeap.Alloc` worth keeping as C# (translated through the new
  pipeline) vs hand-porting to MIR? Trying the translated path first is
  free — if it fails, fall back to hand-port.
