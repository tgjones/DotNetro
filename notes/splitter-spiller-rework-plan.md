# Plan: SplitKit-style splitter + spiller rework (gap-1 completion)

Dedicated follow-on to [`greedy-ra-splitkit-plan.md`](greedy-ra-splitkit-plan.md).
That effort built the greedy allocator (assign → evict → split → spill ladder,
four split kinds) and **solved gap 2** (the `OperandLegalizationPass`, Stage 4a).
It got stuck on **gap 1**: with the isel out-funnels removed, greedy cannot
reconstruct the constrained carry-chain relocations — it falls into split/spill
loops. Four successive fixes each exposed a new failure layer, which is the
CLAUDE.md signal that the *substrate* is wrong, not that it needs another patch.

Decision recorded 2026-06-22: treat funnel removal as a dedicated splitter/spiller
rework. **This document is the plan; no code yet.**

## Why a rework (the four failure layers — these are the requirements)

Each was diagnosed live on `add_i16` (an i16 carry chain = two `adc`s threading the
carry) with greedy default + funnels removed:

1. **Eviction-routing.** The across-clobber value (`%lo`, the first adc result,
   live across the second adc's `$a` def) is the LONGER interval, so greedy assigns
   it `$a` first. The second adc result (`%hi`) then fails, but cannot evict the
   heavier incumbent and cannot split itself → useless spill. The value that must
   move is the incumbent, not the failing newcomer. (Patched in 3c by an
   "incumbent-direction" split; correct but insufficient — see below.)

2. **Non-SSA def-point.** Post-TwoAddress the chain is two-address: the adc result
   vreg is its own tied accumulator, so it has TWO defs (the tied-acc `pseudo.copy`
   AND the adc). The relocation edit keyed off `GetDefinition` (first def = the
   copy, *before* the adc), so it relocated the adc INPUT, not its RESULT. (Patched
   in 4b-i by selecting the latest def before the clobber via slot order; correct,
   still insufficient.)

3. **Split products re-spill.** A relocated value that still can't be placed falls
   through to the pass's store/reload, which doesn't relieve the contention (the adc
   re-pins its result to `$a` every round), so it re-spills every round until the
   round cap.

4. **Greedy spills an ASSIGNABLE flexible value; the loop won't terminate.** After
   the def-point fix, `add_i16` traces as: round 1 split, round 2 split, then
   rounds 3…N spill `%21:any8` — a **flexible** value (full zero-page pool, ~30
   registers) that should never need to spill. So greedy reaches its spill rung for
   a value it should trivially assign, and the reanalysis-driven store/reload never
   relieves it. This is the deepest signal: either interference is over-conservative
   after a heuristic split (the edit didn't actually shorten the range the way fresh
   reanalysis then measures), or assign/interference has a bug — and the
   reanalysis-between-rounds model hides which.

**Root architectural cause.** The current splitter does *heuristic, block-local IR
edits* (mint a copy, redirect later in-block uses) and relies on a *full
reanalysis between rounds* instead of incremental, SlotIndex-precise interval
transfer. That substrate cannot express the precise live-range surgery llvm-mos's
SplitKit does, and the split↔spill loop has no cost model to choose split over
spill or to guarantee termination. Patches to it keep exposing new corners.

## The llvm-mos reference (what "right" looks like)

Read both the output and the source (CLAUDE.md mandate). Local toolchain at
`/Users/timjones/Code/llvm-mos`.

- **`add16` output** (`clang --target=mos -Os -S`): `clc / adc __rc2 / `**`tay`**` /
  txa / adc __rc3 / tax / `**`tya`**` / rts` — the low result is relocated to `$y`
  (a register, not memory) right after adc1, then moved back for the return. No
  spill.
- **Pre-RA MIR** (`llc -stop-before=greedy`): two `%:ac` values live simultaneously
  with NO relocation copy in the IR — the greedy allocator inserts the `tay`/`tya`
  itself via live-range splitting + **register-class inflation** (a split product
  takes the largest legal superclass, so an `ac` piece can land in `$y`/zp).
- **`SplitKit.h` `SplitEditor`** (the surgery API):
  `openIntv` / `enterIntvBefore(SlotIndex)` / `enterIntvAfter` /
  `leaveIntvBefore` / `leaveIntvAfter` / `useIntv(Start,End)` /
  `overlapIntv(Start,End)` / `finish()`, with `transferValues()`,
  `hoistCopies()`, `extendPHIKillRanges()`. Splits are expressed as SlotIndex
  intervals; `finish` rewrites and `transferValues` maps values onto the new
  ranges incrementally — no full reanalysis.
- **`RegAllocGreedy.cpp`** cost model: `tryEvict` /
  `canEvictInterferenceBasedOnCost` (evict only if cheaper than the alternative,
  cascade loop-prevention), `trySplit` → `tryRegionSplit` (SpillPlacement +
  EdgeBundles, OUT of scope) / `tryInstructionSplit` / `tryLocalSplit`
  (`calcGapWeights`), and `LiveRangeStage` (RS_New → RS_Assign → RS_Split →
  RS_Spill → RS_Done) bounding work. Spill weight from `VirtRegAuxInfo` (block
  frequency × refs ÷ size), with the key property that a value cheaply
  *relocatable* (one copy) has low effective cost, so split is preferred to spill.

## Proposed architecture

The crux change: **replace the heuristic block-local split edit + full reanalysis
with SlotIndex-addressed live-range splitting that updates intervals precisely**,
and **give split-vs-spill a cost model with a terminating stage ladder**. Concrete
pieces (each maps to a numbered failure layer above):

1. **SlotIndex addressing in `SplitEditor`** (layers 2, 4). Express every split as
   "open a new interval over `[SlotIndex start, end)`, enter it before/after a
   given instruction's def/use slot, redirect the uses in that range." Build on the
   existing `LiveIntervals` 2-slot numbering (`UseSlot`/`DefSlot`,
   `BaseSlotOf[instr]`) — Irie already has the SlotIndex substrate; what's missing
   is a SplitEditor that addresses program points by slot rather than by
   `GetDefinition` + block index, and that handles multi-def (two-address) vregs by
   construction. Decide: incremental `transferValues` (precise, llvm-faithful, more
   code) vs. keep per-round reanalysis but make the *edit* slot-precise and prove it
   strictly shortens the donor range (cheaper; the reanalysis is then correct
   because the edit is correct). Recommendation: **keep reanalysis, fix the edit** —
   the reanalysis is not the bug, the imprecise edit is; this preserves Irie's
   biggest simplification while removing the failure source. Revisit only if a
   corpus case needs cross-block (PHI) interval transfer.

2. **Register-class inflation of split products** (layer 4). When splitting an
   `ac`-pinned value, the split product must take the largest legal superclass
   (`Anyi8`) so it can land in `$y`/zp — already done (temps minted in the flexible
   class). The flexible-value-spills bug means the *interference* seen for that temp
   is wrong after the heuristic edit; piece 1 (slot-precise edit) should remove it.
   Verify directly: a flexible value must never reach the spill rung unless the
   flexible pool is genuinely exhausted.

3. **Eviction/spill cost model + split-not-spill** (layers 1, 3, 4). Port
   `canEvictInterferenceBasedOnCost`: the failing value may evict an incumbent when
   the incumbent is cheaply *splittable* (relocatable in one copy), not only when
   strictly lighter. And make the ladder prefer split over memory-spill for any
   value whose class has a flexible superclass (the relocatable case). The pass's
   `SpillVregs` store/reload becomes the genuine last resort (truly exhausted
   pressure), not the fallback the chains hit.

4. **Terminating stage ladder** (layers 3, 4). Make `LiveRangeStage` actually bound
   work: a value splits at most once per stage; a split product that still cannot
   assign goes to RS_Spill (memory), never re-split; the donor range strictly
   shrinks each split. Add an assertion that each round's edit strictly reduces a
   well-founded measure (total live-range length over constrained-class vregs), so
   non-termination fails loudly at its cause instead of at the round cap.

**Explicitly OUT of scope** (unchanged from the parent plan): `EdgeBundles` +
`SpillPlacement` global/region split, `InterferenceCache`, last-chance recoloring,
and wiring memory-spill (`pseudo.spill`/`pseudo.reload`) to the asm path. The
corpus does not need them; the staged design leaves seams.

## Staged implementation

Each stage validated by the convergence check (greedy default + funnels removed,
run `dotnet test --project src/Irie.Tests`, then revert) BEFORE touching goldens,
plus a focused unit test on synthetic two-address MIR.

- **Stage R0 — instrument & characterize.** *(DONE — see
  [`splitter-spiller-rework-progress.md`](splitter-spiller-rework-progress.md).)*
  Added a gated RA trace (`IRIE_RA_TRACE`, `src/Irie/Passes/RaTrace.cs`) + one-shot
  post-split IR dump. Characterized all four cases. Two failure modes: **A** —
  `add_i16`/`add_i32` spill the final constrained adc result because the relocation
  temp lands back on `$a` via copy-hint; **B** — `common_subexpr`/`early_return`
  loop forever at the constrained-def-result split rung because the edit is
  block-local but the conflict is cross-block, so the donor range never shrinks.
  **Layer-4 answer: neither over-conservative interference nor an assign bug** —
  the interference is correct; the "any8" value's *allowed set* is actually `{$a}`
  (adc def[0]=Ac), and the heuristic block-local edit simply fails to push it off
  the constrained register.

- **Stage R1 — slot-precise SplitEditor.** Re-express the relocation edit in terms
  of SlotIndexes and multi-def vregs: relocate at the exact producing-def slot,
  redirect exactly the uses in the across-clobber sub-range, and prove the donor
  range strictly shrinks. Unit-test on the two-address `adc`-chain shape directly.
  Re-run the convergence check; expect the chains (`add_i16/-i32`, `sub_i16`) to
  converge to the `tay`/`tya` shape with NO spill.

- **Stage R2 — cost model + split-not-spill + stage ladder.** Eviction-by-cost
  (splittable incumbent), split preferred over memory-spill for relocatable values,
  terminating stage ladder with the well-founded measure assertion. Re-run; expect
  the control-flow class (`common_subexpr`, `early_return`) to converge.

- **Stage R3 — flip default + remove funnels + goldens + measure.** As the parent
  plan's Stage 4c: flip `useGreedy`, remove the seven isel out-funnels +
  `InsertRelocationCopiesForConstrainedDefs`, regenerate goldens the harness way
  (equal-or-better per case; chains must match the llvm-mos `tay`/`tya` shape),
  delete the colourer + duplicated `ComputeAllowedColours`/`TrySplitToRegister`,
  and confirm `irie-report` holds at or beats −6.7%.

## Validation (every stage)

```
dotnet test --project src/Irie.Tests                 # unit + lit
dotnet test --project src/DotNetro.Compiler.Tests    # emulator
dotnet run -c Release --project src/Irie.Tools.Reference   # irie-report headline
```
Convergence check (R1/R2): temporarily flip greedy default + disable the adc/sbc
funnels + `InsertRelocationCopiesForConstrainedDefs`, run the Irie suite, grep for
`did not converge` / `pseudo.spill survived`, **then revert** (greedy stays
flag-gated until R3). Beware the stale-build trap — trust `dotnet test`, not a
one-off `dotnet build src/Irie.Tools.Compiler`.

## Risks

- **Scope creep toward full LLVM RegAlloc.** Stop at R3; the four-layer analysis
  bounds exactly what's needed for the corpus. Don't build EdgeBundles/region split.
- **Incremental `transferValues` temptation.** The recommendation is to keep
  per-round reanalysis and fix the *edit*; only adopt incremental transfer if a
  cross-block case proves it necessary (none in the corpus today).
- **Golden regeneration trap.** Regenerate/verify under `dotnet test`; pin the
  frame-slot nondeterminism cases (`AddressTakenFrameSlot`, `StaticFrameAllocChain`,
  `FrameSlotStructRoundtrip`) to the harness form.
- **Determinism.** Every queue/tie break on ascending vreg id.

## Current committed state (starting point for this rework)

- Greedy RA engine + ladder, 4 split kinds (instruction / constrained-def 1b /
  incumbent 1c / local / around-call), spill weights + cascade eviction — all
  flag-gated (`RegisterAllocatorPass(useGreedy:)`), colourer still default.
- `OperandLegalizationPass` (gap 2 solved), wired, no-op while funnels present.
- The split kinds' def-point fix (4b-i) committed.
- The seven isel out-funnels + `InsertRelocationCopiesForConstrainedDefs` are still
  in place and load-bearing on the default (colourer) path.
