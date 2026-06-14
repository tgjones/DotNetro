# MOS6502 Codegen — Remaining Work Plan

Standalone plan carved out of [`mos6502-codegen-quality-plan.md`](mos6502-codegen-quality-plan.md)
on 2026-06-14, covering only what is **not yet done**. The parent plan's
Workstream A (cross-call value preservation) is complete; this file is the
forward-looking remainder.

## What is already done (context, not work)

- **Workstream A — cross-call value preservation (complete).**
  - Layer 0: `CallerSavedScratch` aligned to the LLVM-MOS convention
    (`A, X, Y, C, N, V, Z, B, RC2..RC19`; callee-saved `D`/`I` dropped) —
    [MOS6502CallLowering.cs:326](../src/Irie/Target/MOS6502/MOS6502CallLowering.cs#L326).
  - Layer 1: RA places live-across-call vregs in callee-saved `RC20..RC31`;
    `PrologueEpilogueInsertionPass`
    ([src/Irie/Passes/PrologueEpilogueInsertionPass.cs](../src/Irie/Passes/PrologueEpilogueInsertionPass.cs))
    + `MOS6502FrameLowering` save/restore them via `pha`/`pla` on the hardware
    stack (recursion-safe, no frame slot).
  - Translator-side `.bss` interim removed: cross-call locals/params now flow as
    ordinary SSA vregs ([IlToMirTranslator.cs:570-600](../src/DotNetro.Compiler/IlToMirTranslator.cs#L570)).
    Only address-taken (`ldloca`) and struct locals stay FrameSlot-bound.
  - Runtime helpers made convention-clean: `runtime.irie` now uses caller-saved
    `$zp2/$zp3` as scratch instead of the callee-saved soft-stack `RC0/RC1`.
- **Workstream B cause #1** (loop pointer landing in `.bss`) — fixed as a side
  effect of Workstream A; the pointer now lives in callee-saved `RC20/RC21`,
  which the runtime helpers don't touch.

## Remaining work (three independent items)

The three items below are mutually independent and can land in any order, with
one sequencing constraint: **C is gated on the RA redesign**, which has no plan
file yet. Recommended order is **1 → 2 → 3** (cheapest/most-isolated first).

---

### Item 1 — Increment strength-reduction (Workstream B #3) — quick win

**DONE 2026-06-14** (commit `c697fed`). Implemented as a target-private post-RA
peephole pass `MOS6502IncrementStrengthReductionPass`, registered in
`MOS6502Target.AddPostRegisterAllocationPasses` between AMS and ParallelCopy. It
matches the post-AMS `clc` + `adc.zp` (or `sec` + `sbc.zp`) window for a `±1`
addend where the result location equals the input location and the carry-out is
dead, and rewrites to `inx`/`dex`/`iny`/`dey`/`inc.zp`/`dec.zp`, deleting the now
dead constant copy and `clc`/`sec`. Added emit rules for `inc.zp`/`dec.zp`/`dex`/
`dey`. Caveat (as the plan permitted): the 16-bit `inc lo / bne / inc hi` (+`dec`
borrow mirror) is **not** implemented — in practice RA keeps a multi-byte
counter's high byte in a register and the low byte's carry-out is live, so neither
byte is foldable; the wide case stays on the generic `adc`/`sbc` chain. Test:
`IncrementStrengthReduction-MachineCode.irie`. Both suites green.

**Goal.** A `ptr + 1` / `i + 1` (and the `-1` mirror) on a zp-resident value
whose input is dead/reused-in-place should lower to `inc zp / bne / inc zp+1`
(16-bit) or a single `inc zp` (8-bit), matching LLVM-MOS, instead of the generic
`arith.addi_with_carry` `adc` chain.

**What already exists.** `Inc`, `Dec`, `IncZp`, `IncZpX`, `DecZp`, `DecZpX`
opcodes are already defined in
[MOS6502Op.cs](../src/Irie/Target/MOS6502/MOS6502Op.cs) (lines 31–32, 161–166).
No new opcodes needed — only a lowering rule that produces them.

**Approach.**
1. Add a peephole/legalizer special-case (likely in
   [MOS6502LegalizerInfo.cs](../src/Irie/Target/MOS6502/MOS6502LegalizerInfo.cs)
   or a small post-isel/AMS rewrite — decide based on where operand widths are
   already known) that pattern-matches `arith.addi %v, #±1` where `%v` is dead
   after the add (single use, def-source reused in place — i.e. the two-address
   form RA would tie anyway).
2. 8-bit: emit `inc`/`dec` on the operand's location. 16-bit: emit the
   `inc zp / bne +skip / inc zp+1` low-then-high-on-carry sequence (and the
   `dec` mirror, which needs a compare-for-borrow since 6502 `dec` sets no carry
   — match LLVM-MOS's `dec`/`lda`/`cmp #$ff`/`bne`/`dec` shape, or restrict the
   first cut to `inc` only and leave `dec` as a follow-up).
3. The match must require the input be the same storage as the output (in-place
   `inc`), so it most naturally runs **after** RA/AMS when physregs are known,
   as a target post-RA pass — or be expressed as a tied-operand isel pattern so
   the two-address pass + RA collapse the source and dest. Prefer the post-RA
   peephole: simplest, and AMS already runs there.

**Sequencing.** Independent of everything; do it first. It's the cheapest win
and exercises no new register-model machinery.

**Test.** A focused MIR lit test in
`src/Irie.Tests/Lit/CodeGen/MOS6502/` with a `ptr + 1` in a loop, asserting
`inc`/`dec` output and absence of `adc`. Full suite green Debug+Release.

---

### Item 2 — Static stack allocation (frame-placement optimization)

**DONE 2026-06-14** (commit `da9d9e0`). Both sub-pieces landed end-to-end.
- 2a: `bool IsNonReentrant` on `MirFunction` + `src/Irie/Passes/Analyses/ReentrancyAnalysis.cs`
  (call graph from `Symbol` operands + one conservative indirect-call edge;
  iterative Tarjan SCC; non-reentrant iff singleton SCC, no self-recursion, not
  interrupt-reachable — interrupt support is a documented extension point, no
  attribute added yet).
- 2b: `src/Irie/Target/MOS6502/MOS6502StaticFrameAllocPass.cs` (module-level pass,
  appended to `AddPostRegisterAllocationPasses`). Bottom-up static-stack layout
  (`base(f) = max over callees of base(c)+footprint(c)`), window `RC20..RC29`
  (`$14`–`$1D`; RC0/RC1 conservatively left alone), `.bss` fallback for overflow,
  reentrant functions, RA-zp collisions, and — the key correctness finding —
  **slots whose address escapes** (passed to a callee / stored / pointer
  arithmetic, e.g. a struct passed to its constructor) must stay on `.bss`.
  Zp-resident slot accesses are rewritten to direct `LDA $nn`/`STA $nn` (reusing
  existing `LdaZp`/`StaZp` emit rules). Frame bytes live in the callee-saved RC
  window so PrologueEpilogueInsertionPass preserves callers' values. Tests:
  `StaticFrameAllocChain` (coloring), `StaticFrameAllocOverflow` (.bss fallback),
  `StaticFrameAllocRecursive` (reentrancy guard), `FrameSlotStructRoundtrip`
  golden updated. Both suites green (incl. emulator-executed `UseStructWithConstructor`).

**Goal.** Replace soft-stack/`.bss` frame storage for memory-resident frame
objects (struct locals, address-taken locals, future register spills) with
fixed zero-page addresses for non-reentrant functions, overlaying disjoint call
subtrees so total zp use = max frame bytes along any single call path. This
speeds up *every* function with a memory-resident frame; it is **not** gated on
Workstream A and Workstream A is not gated on it.

**Two sub-pieces:**

#### 2a — `ReentrancyAnalysis` + `MirFunction.IsNonReentrant`

Target-agnostic interprocedural analysis over the `MirModule` call graph:
1. Build the call graph from `Symbol` operands of call instructions
   (`call.func` / lowered `mos6502.jsr.abs @name`) + one conservative
   indirect-call edge (any indirect call may reach any address-taken function).
2. Tarjan SCCs. A function is non-reentrant iff its SCC is a singleton, it
   doesn't self-recurse, and it isn't reachable from an `[interrupt]` function
   (no interrupt attribute exists yet — add only when first needed).
3. Add `bool IsNonReentrant` (default false) to
   [MirFunction.cs](../src/Irie/Mir/MirFunction.cs) and record the result.

The DotNetro corpus is entirely non-recursive, so every function qualifies
today. The analysis must still exist so a future recursive program is rejected
rather than silently miscompiled — throw a clear "recursion not yet supported"
error if a reentrant function needs a memory-resident frame.

#### 2b — `MOS6502StaticFrameAllocPass` (post-RA, MOS6502-specific)

Registered in `MOS6502Target.AddPostRegisterAllocationPasses`
([MOS6502Target.cs:27](../src/Irie/Target/MOS6502/MOS6502Target.cs#L27)),
alongside the existing AMS + ParallelCopy passes.
1. Take non-reentrant functions and their `FrameSlots`.
2. Lay frames out bottom-up like a static stack: a function's frame base = max
   over its callees of (callee base + callee size). Disjoint from every
   transitive callee's frame; independent subtrees reuse the same zp.
3. Assign each slot a concrete zp address in the reserved window
   (`RC20..RC29` = `$14`–`$1D`, plus `RC0/RC1` if the soft stack stays unused,
   plus anything freed by tightening the trampoline reservation). Account for
   the runtime helpers' fixed `$00`–`$0D` usage when sizing. Overflow → `.bss`.
4. Rewrite slot accesses (`mem.symbol @slot` → `lda.zp/sta.zp` at the assigned
   address) so each becomes a single `LDA $nn`/`STA $nn` instead of the
   indirect-Y `.bss` path.

**Tests.** A two-deep call chain whose frames overlap-by-reuse in the assigned
zp addresses (coloring works), plus a `.bss`-overflow case. Suite green.

---

### Item 3 — Allocatable `imag16` pointer register class (Workstream B #2)

**BLOCKED — not started.** Its stated prerequisite (the graph-coloring RA
redesign, `notes/register-allocator-redesign-plan.md`) does not exist yet, so
this item cannot proceed without first writing and landing that redesign. Left
for a future effort.

**The heaviest lift. Gated on the RA redesign** (the parent plan references
`notes/register-allocator-redesign-plan.md`, which does not yet exist — that
graph-coloring RA redesign is the prerequisite and natural home for a
paired/`imag16` class). Do this **after** the RA redesign lands; do not
retrofit the current linear-scan RA.

**Problem.** Even with the frame slot gone (Workstream A), isel re-parks a
pointer's two bytes into the *fixed* `RC0/RC1`-style scratch pair on every
indirect access — `EmitPointerSetup` in
[MOS6502InstructionSelector.cs](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs)
re-materializes the pair per load/store. To match LLVM-MOS's `lda (zp),Y` with a
loop-invariant base, the pointer must be a loop-carried allocatable 16-bit value
held in one zp pair across iterations.

**Approach (high-level — refine against the RA redesign once it exists).**
1. Introduce an `imag16` register class: a 16-bit value occupying an adjacent zp
   pair the RA can allocate and keep live across a loop. Touches `MOS6502Registers` /
   `MOS6502RegisterClass` / `MOS6502RegisterInfo`.
2. Teach the RA to pair/align two zp bytes for an `imag16` value.
3. Have the indirect-Y load/store isel address the allocated pair directly
   instead of always copying into the fixed scratch pair.

**Payoff.** Closes the last gap to LLVM-MOS's ~12–15-instruction
`@WriteLineString` loop body (a loop-invariant indirect base). Items 1 +
Workstream A already deliver most of that win; this is the remainder.

---

## Explicitly out of scope (carried from parent plan)

- **Per-callee clobber tightening (LLVM IPRA).** Not needed — Workstream A's
  callee-saved model already fixes `@WriteLineString`, and IPRA can't apply to
  opaque `extern`s like `osasci`. At best a later register-pressure optimization.
- **Base-fixed + `iny` hand-written trick.** Only valid for strings < 256 bytes;
  the generic pointer-increment path is the honest target.
- **Soft stack for reentrant functions.** Out of scope; corpus is non-recursive.
  `ReentrancyAnalysis` throws on the unsupported case rather than miscompiling.

## Suggested sequencing summary

| Order | Item | Independence | Notes |
|---|---|---|---|
| 1 | Increment strength-reduction (B#3) | fully independent | quick win; opcodes already exist |
| 2 | Static stack allocation (2a + 2b) | independent track | speeds up every memory-resident frame |
| 3 | `imag16` pointer class (B#2) | **gated on RA redesign** | heaviest lift; do last |
