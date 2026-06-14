# MOS6502 Codegen Quality — Cross-Call Preservation, Pointer & Loop Lowering — Plan

Originally drafted 2026-05-03 against the (now-removed) `MachineFunction` layer.
**Rewritten 2026-06-05** against the current unified-MIR pipeline, the concrete
bug it must fix, and the correct LLVM-MOS calling convention.
**Renamed + rescoped 2026-06-12** (was `static-stack-alloc-plan.md`).

## Scope

This plan covers MOS6502 codegen quality, anchored to LLVM-MOS as the reference
implementation (local toolchain at `/Users/timjones/Code/llvm-mos/build/bin` —
`clang`/`llc`; inspect Machine IR with `llc -stop-after=…`). It spans three
**orthogonal** concerns. The original file conflated the first and third (it was
named `static-stack-alloc-plan.md` but was really about cross-call preservation);
hence the rename and this split.

- **Workstream A — cross-call value preservation** (the original motivating
  content). A value live across a call must survive the callee clobbering
  registers. Mechanisms: a **callee-saved register** — the primary path; LLVM-MOS
  saves/restores these via the **hardware stack** (`pha`/`pla`) — or, as a
  fallback under register pressure, a **memory spill** to the function's frame.
- **Workstream B — pointer & loop codegen quality** (added 2026-06-12). The
  `@WriteLineString` C# rewrite regressed a tight ~8-instruction hand-written loop
  to ~50; closing the gap needs an allocatable pointer register and increment
  strength-reduction. See [Pointer & loop codegen quality](#pointer--loop-codegen-quality-workstream-b).
- **Static stack allocation** — a frame-*placement* optimization (LLVM-MOS's
  `MOSStaticStackAlloc`), **independent of cross-call preservation**. For
  **non-reentrant** functions it gives the frame a fixed address (instead of the
  soft stack) and call-graph-colors disjoint call subtrees to share bytes. It
  answers *where* a function's memory-resident frame objects live, regardless of
  *why* they're in memory. See [Static stack allocation](#static-stack-allocation-orthogonal-frame-placement-optimization).

**How they relate.** Workstream A and static stack allocation are orthogonal:
cross-call preservation via callee-saved registers + `pha`/`pla` needs no static
frame at all (and is recursion-safe), and static stack allocation is worth doing
for any function with memory-resident locals/spills even when nothing is live
across a call. The only link is one-directional and optional — *if* a cross-call
value is preserved by **spilling** (register pressure exceeds the callee-saved
pool), it becomes a frame object, and *that* object's placement is what static
stack allocation optimizes. A *consumes* the placement substrate; it is not built
from it. (DotNetro's shipped interim blurred this by spilling every cross-call
value to a per-function `.bss` slot — a degenerate, uncolored static frame — which
is exactly why the two looked coupled.)

## Why we need this now (Workstream A)

Porting the lit-test corpus to the IL→MIR pipeline (the IL→MIR translator
migration, now complete) surfaced **cross-call value preservation** as a
blocker. The motivating test is `CallNestedMethods`:

```csharp
static void MethodA() {
    var x = 1;
    var y = 2;
    MethodB(x + y);          // x and y must survive this call …
    Console.WriteLine(x + y); // … because they're read again here
}
```

`MethodA`'s `x`/`y` are clobbered by `MethodB`, so `x + y` prints 5 instead of
3. The same problem blocks the whole **class/heap group** (`UseClass`,
`UseNestedClass*`, `CallInstanceMethodOnClass`, `CallOverriddenMethodOnClass`)
and the **struct group**, where a `this` pointer / object reference is live
across a constructor or method call.

This is **not** a register-pressure problem (the RA's zero-page widening, commit
`c5aedfa`, already handles pressure). It is a *preservation* problem: a value
live across a call must end up either in a **callee-saved** register (which the
callee promises to restore) or in memory the callee won't touch.

## The calling convention (LLVM-MOS C convention)

Source: <https://llvm-mos.org/wiki/C_calling_convention>. Now documented in the
header of [MOS6502CallLowering.cs](../src/Irie/Target/MOS6502/MOS6502CallLowering.cs).

| Class | Registers |
|---|---|
| **Caller-saved** (volatile; callee may clobber; caller preserves if needed across a call) | `A, X, Y, C, N, V, Z, RC2..RC19` |
| **Callee-saved** (callee that clobbers them must save+restore; survive a call) | `PC, S, D, I, RC0, RC1, RC20..RC31` |

Args/returns are passed in `A, X, RC2, RC3, …` LSB-first. `RC0`/`RC1` are the
soft-stack pointer (callee-saved, currently reserved+unused). With `RC(n)` →
zero-page byte `n`, the zp map is:

| zp | slots | class / role |
|---|---|---|
| `$00`–`$01` | RC0–RC1 | callee-saved — soft-stack pointer (reserved) |
| `$02`–`$0F` | RC2–RC15 | caller-saved — also CC arg/return slots |
| `$10`–`$13` | RC16–RC19 | caller-saved |
| `$14`–`$1D` | RC20–RC29 | **callee-saved — the pool for cross-call values** |
| `$1E`–`$1F` | RC30–RC31 | callee-saved — indirect-call trampoline `JMP ($1E)` |

So the cross-call "frame" budget in zero page is essentially `RC20..RC29`
(`$14`–`$1D`, 10 bytes), with `RC0`/`RC1` also callee-saved but earmarked for
the soft stack. Beyond that, frames must spill to absolute `.bss` memory.

## Root cause in the current code

`MOS6502CallLowering.CallerSavedScratch`
([MOS6502CallLowering.cs](../src/Irie/Target/MOS6502/MOS6502CallLowering.cs))
currently encodes a **stopgap**, not the real convention:

- It marks only `A, X, Y, C, N, Z, V, I, D, B, RC2..RC15` as clobbered by a
  call. Versus the real convention it (a) stops the RC caller-saved range at
  `RC15` instead of `RC19`, and (b) wrongly includes the callee-saved flags
  `D` and `I`.
- `RC16..RC31` are deliberately left out so the RA has a pool of "effectively
  callee-saved" zp slots for cross-call values — **but no function actually
  saves/restores them**, so the contract is unenforced.

Result: the RA parks a cross-call value (e.g. `MethodA`'s `x`) in `RC16+`,
`MethodB` reuses the same slot for its own value, and `x` is destroyed. The two
problems to fix are therefore: **align the clobber set to the convention**, and
**make the callee-saved registers actually preserved** (prologue/epilogue
save/restore — see Workstream A below).

## Relevant current architecture (what exists to build on)

### Frame slots + FrameLowering already exist

`MirFunction.FrameSlots` (`List<FrameSlot>`, `FrameSlot(int Index, IRType Type,
string SymbolName)`) and [FrameLoweringPass](../src/Irie/Passes/FrameLoweringPass.cs)
(runs first, before the legalizer) materialize each slot as a zero-init module
**`.bss` MirGlobal** and rewrite `mem.frame_addr <i>` → `mem.symbol
@<slot_name>`. Slots are per-function, uniquely named, hence never overlap — a
value in a frame slot already survives any call. Today they target **absolute
memory**, reached via the generic `mem.load/store` → indirect-Y lowering (a zp
pointer-pair setup + `LDA/STA ($zp),Y`): correct but slow and itself clobbers a
scratch zp pair + `$a`/`$y`. The translator does not emit FrameSlots yet.
Placing cross-call frames in **zero page** instead makes each access a single
`LDA $nn` / `STA $nn` (ops already exist) — the optimization this plan wants.

### Pipeline & RA

Pass order (`iriec` / `CompilerDriver.Compile`): FrameLowering →
AbiLowering → Legalizer → InstructionSelector → PhiElimination → TwoAddress →
**RegisterAllocator** → CopyElimination → post-RA (AddressingModeSelector,
ParallelCopy) → PseudoExpansion → MachineCodeEmitter.

`RegisterAllocatorPass` ([src/Irie/Passes/RegisterAllocatorPass.cs](../src/Irie/Passes/RegisterAllocatorPass.cs))
is clobber-aware linear-scan (livein widening to `any8`, result-preservation
copies, `ComputeClobberSlots` from `JSR` implicit-defs, physreg source-liveness,
the `$a`-scratch model). **It throws on any unsatisfiable allocation — spilling
is not implemented**, and it currently has no notion of callee-saved registers
or prologue/epilogue.

### Hand-written runtime helpers constrain the layout

`runtime.irie` helpers (`@WriteLineInt32`, `@WriteLineString`,
`@WriteLineBoolean`, `@ReadLine`, `@Beep`, OS wrappers, trampoline) are
hand-written, already-lowered MIR using **fixed** zp slots — measured footprint:
`RC0..RC13` (≈ `$00`–`$0D`) plus OS control-block addresses (`$37`–`$3B`). Note
they touch `RC0`/`RC1` (callee-saved soft-stack!) as scratch and do **not**
save/restore anything — a latent convention violation. Importantly they do
**not** touch `RC20..RC31`, so cross-call values placed there are safe from the
runtime even before the helpers are made convention-clean. Any prologue/epilogue
scheme (below) must eventually either make the helpers convention-correct or
keep them off callee-saved registers callers rely on.

## Reentrancy: precondition for fixed-address storage

Any per-function storage at a **single fixed address** — the `.bss` cross-call
spill interim, and (later) static-stack-allocated zp frames — is only correct if
the function is **non-reentrant** (no direct/mutual recursion, not reachable from
an interrupt handler). A recursive function reusing one static save area would
corrupt its own outer activation; it must use the soft stack instead.

This precondition is the gate on **static stack allocation** specifically — it is
*not* a constraint on cross-call preservation as such: callee-saved registers
saved via `pha`/`pla` (the primary Workstream-A mechanism) use the hardware stack
and are recursion-safe by construction. It only bites when a cross-call value is
*spilled* to a fixed-address frame.

The DotNetro corpus is entirely non-recursive with no interrupt handlers, so
every function qualifies today. The detection (`ReentrancyAnalysis`, below) must
still exist so a future recursive program is rejected/handled rather than
silently miscompiled.

## Workstream A — cross-call value preservation

The target model is the LLVM-MOS one: **cross-call values live in callee-saved
registers, and each function saves/restores the callee-saved registers it
clobbers in its prologue/epilogue via `pha`/`pla` on the hardware stack.** Values
that don't fit the callee-saved pool spill to the function's frame as a fallback
(and *that* frame's placement is the separate
[static stack allocation](#static-stack-allocation-orthogonal-frame-placement-optimization)
concern — not part of this mechanism). Built in two layers (Layer 0 + Layer 1)
that together unblock the corpus.

### Layer 0 — align the clobber set to the convention (precondition)

In `MOS6502CallLowering.CallerSavedScratch`: extend the RC caller-saved range to
`RC2..RC19`, drop the callee-saved flags `D` and `I`. After this, `RC20..RC31`
are the only RC slots the RA may treat as surviving a call — and only once Layer
1 actually preserves them. Land Layer 0 together with Layer 1 (or behind a flag)
so the suite is green at each commit: on its own, Layer 0 turns the silent
cross-call miscompile into a deterministic RA failure ("value live across a
clobber, no callee-saved storage").

### Layer 1 — callee-saved register preservation (the fix)

Two cooperating pieces:

1. **RA may place a value whose live range crosses a call into a callee-saved
   register (`RC20..RC31`).** The clobber-aware linear scan already refuses
   caller-saved physregs for such ranges (they're clobbered by the `JSR`); make
   the callee-saved RC slots available/preferred for them. (If none are free —
   pressure beyond ~10 cross-call bytes — fall back to a caller-side spill of
   the value to a static frame slot around the call; see note below.)
2. **Prologue/epilogue callee-saved save** — a new MOS6502-specific **post-RA**
   pass (`PrologueEpilogue` / `CalleeSavedSpillPass`): for each callee-saved
   register a function actually defines, save it to the **hardware stack** at
   entry (`lda $nn; pha`) and restore before every return (`pla; sta $nn`), as
   LLVM-MOS does. This is what makes `RC20..RC31` genuinely survive a call — the
   callee that reuses one first preserves the caller's value — and it is
   **recursion-safe by construction** (hardware stack), so it needs no frame slot
   and no reentrancy precondition. (Note: this is distinct from spilling a
   cross-call *value* to memory under pressure, below; that spill is a frame
   object whose placement is the static-stack-allocation concern.)

**Simpler interim alternative (caller-side spill).** If full callee-saved
preservation proves too large for a first cut, an equivalent-correctness
shortcut treats *all* registers as caller-saved and, for each value live across
a call, stores it to the owning function's static FrameSlot before the call and
reloads after (a `CallCrossingSpillPass` before the Legalizer emitting generic
`mem.*` to `.bss` slots). This needs **no** callee cooperation (so the
hand-written runtime helpers need no change) and reuses FrameLowering verbatim —
at the cost of spilling at every cross-call point even when the callee wouldn't
have clobbered the value, and slow indirect-Y `.bss` access. **Recommended
sequencing: ship the caller-side `.bss` version first to unblock the corpus and
prove the end-to-end path, then migrate to the callee-saved-register model for
efficiency.**

Either way the **bar is the same: un-hold and pass `CallNestedMethods`
end-to-end; full suite green Debug+Release; no regression in the ported Lit/Mir
tests.** Regenerate `CallFuncCrossLive-*` goldens (they encode the old stopgap).

## Static stack allocation (orthogonal frame-placement optimization)

LLVM-MOS's `MOSStaticStackAlloc`. This is a general **frame-placement**
optimization, *independent of Workstream A*: it improves where a function's
**memory-resident frame objects** live — spilled locals, address-taken locals,
register spills, and (only as one case) cross-call values that spilled rather
than landing in a callee-saved register. Default 6502 frame storage is the soft
stack (`RC0/RC1`-relative, slow); this pass replaces it with fixed addresses for
non-reentrant functions and overlays disjoint call subtrees. It is worth doing
for any function with a memory-resident frame even if nothing is live across a
call, and Workstream A needs it only for the spill fallback.

### Reentrancy detection (`ReentrancyAnalysis`) — the precondition

Target-agnostic interprocedural pass over the `MirModule` call graph (the gate
described under [Reentrancy](#reentrancy-precondition-for-fixed-address-storage)):

1. Build the call graph from `Symbol` operands of call instructions
   (`call.func` / lowered `mos6502.jsr.abs @name`) + a conservative
   indirect-call edge (any indirect call may reach any address-taken function).
2. Tarjan SCCs. Mark a function non-reentrant iff its SCC is a singleton, it
   doesn't self-recurse, and it isn't reachable from an `[interrupt]` function
   (no interrupt attribute exists yet — add one only when needed).
3. Record `IsNonReentrant` on `MirFunction` (new bool, default false).

Fixed-address frames are used only for non-reentrant functions. Reentrant
functions must use the soft stack (`RC0`/`RC1` push/pop) — not implemented; for
now throw a clear "recursion not yet supported" error if a reentrant function
needs a memory-resident frame. (No corpus test hits this.)

### Static-frame placement with call-graph coloring

A new MOS6502-specific post-RA pass, **`MOS6502StaticFrameAllocPass`**, places the
per-function frame objects (spilled locals, register spills, and any cross-call
spill fallbacks) in **zero page** and reuses the area across non-overlapping call
paths:

1. Take non-reentrant functions and their frame slots from `ReentrancyAnalysis`.
2. Lay frames out like a static stack, bottom-up: a function's frame base = max
   over its callees of (callee base + callee size). This makes a function's
   frame disjoint from every transitive callee's frame (so a value spilled across
   a call survives) while letting independent subtrees reuse the same zp. Total
   zp use = max frame bytes along any single call path.
3. Assign each slot a concrete zp address inside the reserved window
   (`RC20..RC29` = `$14`–`$1D`, plus `RC0/RC1` if the soft stack is unused, plus
   anything freed by tightening the trampoline reservation). Overflow spills to
   `.bss`.
4. Rewrite slot accesses to `lda.zp/sta.zp` at the assigned address. Account for
   the runtime helpers' fixed `$00`–`$0D` usage when sizing the window.

## Implementation order

1. **Layer 1 (caller-side `.bss` interim) — DONE 2026-06-10. Layer 0 NOT done
   (not needed by the interim).** Implemented **in the translator, not as the
   originally-envisioned `CallCrossingSpillPass`**: `IlToMirTranslator`
   (`ComputeCrossCallLive` + parameter entry-spill) classifies every local and
   parameter live across a `call`/`callvirt`/`newobj` as a FrameSlot, so those
   values live in `.bss` and never occupy a register across the call. Because no
   cross-call value is ever register-resident at a call, `CallerSavedScratch`
   was left as-is (the `RC2..RC19` / drop-`D`/`I` Layer-0 alignment was
   unnecessary) and the RA-level spill path is untouched. Bar met:
   `CallNestedMethods` green end-to-end, full suite green, un-held. Two
   incidental bugs were fixed en route: (1) `TranslateStloc` for a FrameSlot
   *primitive* local used an I16 placeholder width → `mem.store.i16` of an i32
   value (now uses the local's natural CLR width, matching `TranslateLdloc`);
   (2) `MOS6502InstructionSelector`'s pointer-setup cache (`_currentPointerKey`)
   was not invalidated across a `jsr`, so a slot pointer set up before a call was
   reused after the call had clobbered the shared `$zp0/$zp1` indirect pair.
2. **Migrate Workstream A to the callee-saved-register model** — DONE 2026-06-14. RA uses
   `RC20..RC31` for cross-call ranges + a post-RA `CalleeSavedSpillPass`
   prologue/epilogue (`pha`/`pla` on the hardware stack); apply the Layer 0
   `CallerSavedScratch` alignment (`RC2..RC19`, drop `D`/`I`) that the interim
   skipped. Make the runtime helpers convention-clean (don't clobber callee-saved
   regs, or save/restore them). **Remove the translator-side interim first**
   (`IlToMirTranslator.ComputeCrossCallLive`, the cross-call branches of
   local/param classification, and the parameter entry-spill + memory-backed
   `ldarg` path) — otherwise cross-call locals/params stay forced into `.bss` and
   never reach the RA as live-across-call vregs, so the callee-saved model has
   nothing to place in `RC20..RC31` and the migration buys no efficiency. After
   removal, those values flow as ordinary SSA vregs and the RA +
   `CalleeSavedSpillPass` keep them in callee-saved registers. (Address-taken
   `ldloca` locals and struct locals must STAY FrameSlot-bound — only the
   cross-call-driven classification is removed.) Validate efficiency improves and
   suite stays green.

Independent track (any time after Layer 1): **static stack allocation** —
`ReentrancyAnalysis` + `MirFunction.IsNonReentrant`, then
`MOS6502StaticFrameAllocPass` (zp frames with call-graph coloring, `.bss`
overflow fallback). Speeds up *every* memory-resident frame (the `.bss` interim's
cross-call spills today, plus future spilled/address-taken locals); not gated on
the Workstream-A migration and vice versa.

The struct/class/heap groups depend on the cross-call interim (above) plus their
own work (FrameSlot locals + pointer-load isel for structs; ManagedHeap + vtables
+ callvirt for classes). Cross-call preservation is the shared prerequisite for
anything with a value live across a call.

## Pointer & loop codegen quality (Workstream B)

Added 2026-06-12 after the `@WriteLineString` rewrite. The hand-written
`@WriteLineString` MIR (a ~8-instruction loop: pointer parked in `$zp`, `Y`
walks the string, `lda ($zp),Y` / `cmp #0` / `beq` / `jsr osasci` / `iny` /
loop) was replaced by a translated C# `unsafe` byte-loop. Functionally correct,
but the generated MIR is ~50 instructions: the loop-carried pointer lands in a
`.bss` frame slot, is reloaded + 16-bit-`adc`-incremented + re-parked into the
`$zp0/$zp1` indirect pair *every iteration*.

Reference: the same C compiled with LLVM-MOS (`-O2`) is a ~15-instruction loop —
the pointer is an `imag16` virtual register the allocator keeps in a zero-page
pair across the call, and the increment is `inc zp / bne / inc zp+1`. Machine IR
shows the call as `JSR @osasci, mos_csr, implicit $a` (the pointer survives via
the callee-saved CC mask).

Three causes; the first is Workstream A, the other two are the new work:

1. **Pointer goes to a `.bss` frame slot** — because it's live across `osasci`
   and the RA has no callee-saved registers / no spill. **Fixed by Workstream A**
   (Layer 1 callee-saved migration): the pointer lands in callee-saved
   `RC20/RC21`, which the runtime helpers (incl. `osasci`) already don't touch,
   so it survives with no frame slot. Nothing B-specific to do here.

2. **No allocatable pointer register (the structural piece).** Even with the
   frame slot gone, isel re-parks the pointer's two bytes into the *fixed*
   `RC0/RC1` scratch pair on every indirect access (`EmitPointerSetup` in
   [MOS6502InstructionSelector.cs](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs)
   re-materializes the pair per load/store). To match LLVM-MOS's `lda (zp),Y`
   with a loop-invariant base, the pointer must be a **loop-carried allocatable
   16-bit value** held in one zp pair across iterations, not fixed scratch.
   - Introduce an `imag16`-style register class (a 16-bit value occupying an
     adjacent zp pair) that the RA can allocate and keep live across the loop,
     and have the indirect-Y load/store address it directly instead of always
     copying into `RC0/RC1`. This is the heaviest lift — it touches the register
     model, the RA (pairing/alignment of two zp bytes), and the indirect-Y isel.
   - Interaction with the RA redesign: the graph-coloring RA redesign
     (`notes/register-allocator-redesign-plan.md`) is the natural place to add a
     paired/`imag16` class; sequence this after that lands rather than retrofitting
     the linear-scan RA.

3. **Increment strength-reduction.** `ptr + 1` legalizes to a generic
   `arith.addi_with_carry` chain (2-byte `adc`). The dialect already has
   `IncZp` / `Inc` ([MOS6502Op.cs](../src/Irie/Target/MOS6502/MOS6502Op.cs)).
   Add a legalizer/peephole special-case: an `add`/`sub` of a zp-resident value
   and a `±1` immediate (where the input is dead / reused in place) lowers to
   `inc zp / bne / inc zp+1` (and the `dec` mirror), matching LLVM-MOS. Cheap and
   mostly independent of (2); do it first as the quick win once (1) removes the
   frame slot.

Explicitly **out of scope / lower priority:**

- **Per-callee clobber tightening (LLVM IPRA).** LLVM *has* this
  (`--enable-ipra`) but empirically LLVM-MOS does **not** rely on it here — even
  for a visible leaf clobbering only `A`, the call kept the full `mos_csr` mask
  and the caller used a callee-saved register. And for an opaque `extern` like
  `osasci`, IPRA cannot apply at all. So it is **not needed** to fix
  `@WriteLineString` (Workstream A's callee-saved model already does), and is at
  best a later register-pressure optimization for functions whose cross-call live
  set exceeds the ~10-byte callee-saved pool. Not pursued here.
- **Base-fixed + `iny` (the hand-written trick).** Beats LLVM-MOS but only valid
  for strings < 256 bytes (`Y` is 8-bit); the generic pointer-increment path is
  the honest target. Only special-case if a < 256 length guarantee is available.

**Realistic target:** Workstream A + (3) lands most of the win (no frame slot, no
`adc` chain → ~llvm-mos's ~12–15-instruction body); (2) closes the remaining gap
to a loop-invariant indirect base.

## Tests

- `src/DotNetro.Compiler.Tests/Lit/Mir/CallNestedMethods.cs` — the end-to-end
  bar for cross-call preservation (emulated == .NET: `MethodC`'s `x`, then
  `MethodA`'s `x+y` = 3, twice).
- `src/Irie.Tests/Lit/CodeGen/MOS6502/` — a focused MIR lit test with a value
  live across a `call.func`, asserting the value is preserved (frame-slot
  store/reload, or callee-saved-register + prologue/epilogue save) and not held
  in a caller-saved register across the call. Regenerate `CallFuncCrossLive-*`.
- Static stack allocation: a two-deep call chain whose frames overlap-by-reuse in
  the assigned zp addresses (coloring works), plus a `.bss`-overflow case.

## Open questions

- **Cross-call mechanism**: ship the caller-side `.bss` spill first (simplest, no
  callee cooperation, reuses FrameLowering) — DONE — then migrate to callee-saved
  registers + `pha`/`pla` prologue/epilogue, or go straight to the callee-saved
  model to match LLVM-MOS exactly? (Done: interim `.bss` first; migration next.)
- **Runtime helpers vs the convention**: they clobber `RC0`/`RC1` (callee-saved
  soft stack) and never save anything. Under the callee-saved model they must be
  made convention-clean. Under the caller-side-spill interim they can stay as-is.
- **Soft stack for reentrant functions**: out of scope now (corpus is
  non-recursive). Decide later whether to implement `RC0`/`RC1` push/pop or keep
  rejecting recursion.
- **zp budget (static stack allocation)**: is `RC20..RC29` (10 bytes, optionally
  + `RC0/RC1`) enough for the corpus's deepest frame path, or must some frames
  spill to `.bss`? Measure when building the pass; the `.bss` fallback keeps it
  correct regardless.
