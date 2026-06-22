# Plan: greedy register allocator + SplitKit analogue (Option B)

Implementation plan for **Option B** of
[`live-range-splitting-plan.md`](live-range-splitting-plan.md) — replace Irie's
graph-colouring allocator core with an LLVM/llvm-mos-style **greedy** allocator
that performs real **live-range splitting**, so the hand-coded isel "funnel"
copies (manual splits) disappear by construction.

Decision recorded 2026-06-21: the measured residual favoured Option A, but the
maintainer chose B — the design proven on this exact target, which the corpus
directly validates.

## What "done" looks like

- `MOS6502InstructionSelector` stops emitting out-funnels (the `Ac → Anyi8`
  `pseudo.copy` after every forced-`$a` def: adc/sbc results, load results, EOR).
  isel emits class-constrained vregs + generic copies only; the allocator splits.
- The four manual-split funnel sites (adc/sbc 334/427/477, loads 1136/1172/1192,
  EOR 939) are deleted, and `common-subexpr` / `early-return` (which spill today
  when the funnel is removed) allocate cleanly via allocator splitting.
- `irie-report` headline holds at or beats today's **-6.7%**, with the
  splitting-residual cases (`live-across-call`, `early-return`) improving toward
  the llvm-mos `.s`.
- Full Irie + DotNetro.Compiler lit suites stay green (goldens regenerated the
  harness way).

## Substrate inventory — what Irie already has (reuse, don't rebuild)

Read during planning; these are the LLVM-equivalents already present:

| llvm-mos / LLVM piece | Irie equivalent (present) | File |
|---|---|---|
| `SlotIndexes` (use/def sub-slots) | `LiveIntervals` 2-slot numbering (`UseSlot`/`DefSlot`) | `Passes/Analyses/LiveIntervals.cs` |
| `LiveInterval` (segments + holes) | `LiveInterval` / `LiveSegment` (normalize, overlap, covers) | same |
| `LiveIntervals` analysis | `LiveIntervalsAnalysis` (backward dataflow + per-block segments, physreg-aware) | `Passes/Analyses/LiveIntervalsAnalysis.cs` |
| `LiveRegMatrix` interference query | `LiveIntervals.Overlaps(vreg, physReg)` / `Interfere(v,v)` | `LiveIntervals.cs` |
| `TargetRegisterInfo` (alloc order, classes, CSR) | `TargetRegisterInfo` incl. `GetCalleeSavedRegisters`, `GetShortRangeGprPreference`, `FlexibleI8ClassId` | `Target/TargetRegisterInfo.cs` |
| Spiller (store/reload, remat) | `RegisterAllocatorPass.StoreReloadSpill` / `Rematerialize` (emit `pseudo.spill`/`pseudo.reload`) | `Passes/RegisterAllocatorPass.cs` |
| `tryInstructionSplit` (partial) | `RegisterAllocatorPass.TrySplitToRegister` | same |
| Callee-saved save/restore | `PrologueEpilogueInsertionPass` + `FrameLowering.EmitCalleeSavedSpills/Restores` | `Passes/PrologueEpilogueInsertionPass.cs`, `Target/FrameLowering.cs` |
| Coalescing (post-RA cleanup) | `CopyEliminationPass` + iterated coalescing (being replaced) | `Passes/CopyEliminationPass.cs` |

## Gap inventory — what the greedy core needs that Irie lacks

1. **Priority-ordered allocation queue** (LLVM `enqueue`/`dequeue` by interval
   size/stage). Irie's colourer has no per-interval queue — it works the whole
   interference graph at once.
2. **`tryAssign`** — pick a free physreg from the allocation order honouring the
   class intersection + copy hint. (The class-intersection logic already exists
   in `GraphColouringAllocator.ComputeAllowedColours` — lift it out.)
3. **`tryEvict`** — evict lower-spill-weight interferences to free a physreg,
   with **cascade** loop-prevention. New.
4. **`SplitEditor` analogue** — the mechanical core: open a new interval, insert
   `pseudo.copy` at split boundaries (`enterIntvBefore` / `leaveIntvAfter`),
   rewrite the uses in the split region to the new vreg, and let the next
   `LiveIntervalsAnalysis` recompute liveness (Irie recomputes intervals each
   round already, so we get `transferValues` "for free" via reanalysis). New,
   but small because we lean on reanalysis instead of incremental updates.
5. **`trySplit`** staged: `tryInstructionSplit` (have it), `tryLocalSplit`
   (gap-weight split within one block), and **split-around-call / region split**
   (the `live-across-call` case). New.
6. **`LiveRangeStage`** (RS_New → RS_Split → RS_Spill → RS_Done) — bounds
   splitting so it terminates (a split product that still can't allocate goes
   straight to spill). New, but trivially a `Dictionary<int,Stage>`.
7. **Spill weight / block frequency** — LLVM weights by loop depth. Irie has no
   block-frequency analysis; we approximate with a **loop-depth estimate** from
   the CFG (back-edges) — enough for the corpus; can be refined later.

**Deliberately OUT of scope** (LLVM has them; the corpus doesn't need them, and
they carry the most substrate cost): `EdgeBundles` + `SpillPlacement` global
region splitting across many blocks, `InterferenceCache`, last-chance recoloring,
hint recoloring. The staged design leaves clean seams to add these if a future
corpus case demands them — but per CLAUDE.md we do not build speculative
machinery.

## Guiding principle: minimal-but-correct, directionally similar

The deliverable is a **skeleton that is directionally similar to llvm-mos from
day one** — the full greedy `selectOrSplit` *control-flow ladder* (assign →
evict → split → spill, gated by the `LiveRangeStage`) is present and correct in
Stage 0, with each rung implemented minimally (eviction starts as a no-op stub;
splitting starts as the one split kind we already have). Later stages flesh out
the *quality* of individual rungs — they do not change the architecture. This is
preferred over either a hacky shortcut or a full-fidelity port: it captures the
right shape early and we build on it. (See [[feedback_minimal_correct]].)

## Architecture

New file `Passes/GreedyRegisterAllocator.cs` (the engine, `internal`, replacing
`GraphColouringAllocator`). `RegisterAllocatorPass` stays the wired
`MirFunctionPass` wrapper but its body becomes the greedy driver loop:

```
RegisterAllocatorPass.Run(function):
    InsertRelocationCopiesForConstrainedDefs   # KEEP for now; remove in Stage 4
    loop:
        intervals = LiveIntervalsAnalysis.Compute(function)   # reanalyse
        greedy = new GreedyRegisterAllocator(function, registerInfo, intervals, stages)
        result = greedy.Run()
        if result.Assigned: break
        # result carried IR edits (splits/spills); loop reanalyses
    ApplyAssignment; ClearVRegAnnotations; RecomputeBlockLiveIns
```

`GreedyRegisterAllocator.Run` mirrors `selectOrSplitImpl`:

```
enqueue all vregs by priority (size, stage)
while queue not empty:
    vreg = dequeue()
    phys = tryAssign(vreg)                       # free reg in class∩, hint-first
    if phys: assign; continue
    if stage(vreg) != RS_Split:
        phys = tryEvict(vreg)                     # evict lighter interferences
        if phys: assign; continue
    if stage(vreg) < RS_Split:                    # defer once for full picture
        stage(vreg) = RS_Split; requeue(vreg); continue
    if stage(vreg) < RS_Spill:
        if trySplit(vreg): return EDITED          # IR changed → caller reanalyses
    spill(vreg); return EDITED
return ASSIGNED(map)
```

Because Irie reanalyses intervals between rounds (it already does this in the
spill loop), the SplitEditor does **not** need LLVM's incremental
`transferValues`/`extendPHIRange` — it only needs to (a) mint a new vreg in the
right class, (b) insert the boundary `pseudo.copy`s, (c) rewrite the in-region
uses. The next round's fresh `LiveIntervalsAnalysis` reconstructs all liveness.
This is the single biggest simplification the existing architecture buys us.

`tryEvict` and assignment need a *committed* vreg→physreg map within one Run (so
interference is checked against already-placed vregs). Add a `LiveRegMatrix`
analogue: a `Dictionary<int physReg, LiveInterval>` of assigned-vreg unions, with
`CheckInterference(vreg, physReg)` = `intervals.IntervalOf(vreg).Overlaps(union)`
and a precoloured-physreg seed from `intervals.PhysRegIntervals`.

## Staged implementation (each stage independently shippable + measured)

**Stage 0 — the greedy skeleton (minimal but directionally complete).** Add
`GreedyRegisterAllocator` containing the **whole `selectOrSplit` ladder** so the
architecture matches llvm-mos from the start: priority queue, `LiveRegMatrix`
analogue, `LiveRangeStage` map, `tryAssign` (real — class∩ + hint-first, lifted
from the colourer's `ComputeAllowedColours` + colour-preference order into a
shared helper), `tryEvict` (**stub returning none** — the call site exists so the
ladder is shaped correctly), `trySplit` (**minimal — the one split kind we
already have**, the `TrySplitToRegister` instruction-split logic moved into the
editor), and spill (existing remat / store-reload). Wire behind a flag; keep
`GraphColouringAllocator` as default during bring-up. Validate: greedy must
allocate the straight-line `basics/*` cases identically. *No golden change for
those.* This is the skeleton every later stage builds on without reshaping it.

**Stage 1 — flesh out `tryEvict`.** Replace the stub: spill weights (loop-depth
approximation) + cascade loop-prevention. Makes greedy competitive on non-split
cases. Validate full lit + `irie-report`; expect `basics/*` parity, possible
movement on branchy cases.

**Stage 2 — flesh out splitting: SplitEditor + `tryLocalSplit`.** Build out the
SplitEditor analogue (open interval, boundary copies, region-use rewrite — leans
on next-round reanalysis, no incremental `transferValues`) and add `tryLocalSplit`
(gap-weight split within one block) alongside the instruction-split from Stage 0.
This is the lever for `early-return` (prompt freeing along short paths).
Validate: `early-return` allocates without the manual funnel.

**Stage 3 — split-around-call (the `live-across-call` lever).** A value live
across a call (whose clobbers are explicit implicit-defs on the call instr, so
they already appear in `PhysRegIntervals`) gets split: keep it in a callee-saved
register across the call (relocate via copy), letting
`PrologueEpilogueInsertionPass` emit the save/restore. A single contiguous
across-call region — no edge bundles needed. Validate against
`pressure/live-across-call.s` (llvm-mos relocates `a` to `__rc16` + stack
save/restore).

**Stage 4 — flip the default and delete the manual funnels.** Make greedy the
default; delete `GraphColouringAllocator`, the isel out-funnels (adc/sbc/load/
EOR), and `InsertRelocationCopiesForConstrainedDefs` (now the allocator's job).
Regenerate every affected golden the harness way (run under `dotnet test`, per
the CLAUDE.md golden-regeneration warning — do NOT trust a standalone `iriec`
run; this session the Release `irie-report` showed *no change* when the Debug
suite showed spills). Re-run `irie-report`; confirm the headline holds or
improves and no case regresses.

**Future (NOT in this effort; only if a corpus case demands it) — region/global
split.** `EdgeBundles` + spill-placement for multi-block global splitting, and
wiring the memory-spill (`pseudo.spill`/`pseudo.reload`) lowering to the asm path
(unblocks `pressure/many-calls`). These are the llvm-mos bells-and-whistles we
deliberately leave out; the Stage 0 skeleton has clean seams for them.

## Progress log

### Stage 0 — DONE (2026-06-21, not yet measured against converted corpus)

Greedy skeleton landed behind the `useGreedy` flag; default path is the colourer,
untouched. All green: Irie 188/188 (183 + 5 new), DotNetro.Compiler 21/21,
`irie-report` unchanged at **-6.7%** (greedy not on any default path).

**New files**
- `src/Irie/Passes/GreedyRegisterAllocator.cs` — the engine. Full `selectOrSplit`
  ladder present: priority queue (largest-interval-first, ties by ascending vreg
  id) → `TryAssign` (real: class∩ allowed set + copy-hint-first preference) →
  `TryEvict` (**stub → null**) → defer-to-second-round (New→Split) → `TrySplit`
  (**stub → false**) → spill (records `SpilledVregs`, returns null). `LiveRegMatrix`
  analogue is `_assignedTo` (physreg → committed-vreg list) + precoloured-physreg
  busy windows queried directly via `LiveIntervals.Overlaps`. `LiveRangeStage`
  enum {New, Split, Spill}; the stage map is owned by the pass and persists across
  rounds.
- `src/Irie/Passes/RegisterAllocationSupport.cs` — shared `CollectReferencedVregs`
  + `ComputeAllowedColours` (class intersection). **Canonical copy used by greedy;
  the colourer still has its own private equivalent** (left untouched during
  bring-up to avoid risk to the default path). The two MUST stay in lockstep until
  the colourer is deleted in Stage 4 — that deletion removes the duplication.
- `src/Irie.Tests/Passes/GreedyRegisterAllocatorTests.cs` — 5 tests via
  `RegisterAllocatorPass(useGreedy: true)`: copy-hint assign, no-hint fallback,
  def/use boundary sharing, interference→distinct regs, spill-rung-reachable.

**Edits**
- `src/Irie/Passes/RegisterAllocatorPass.cs` — primary ctor gained
  `bool useGreedy = false`; the spill loop branches engine and feeds one shared
  `spilledVregs` into the existing `SpillVregs`. A persistent `greedyStages` dict
  threads stages across rounds. (Note: `InsertRelocationCopiesForConstrainedDefs`
  still runs for BOTH engines for now — it is removed in Stage 4.)

**Key design choices made**
- Reanalysis over incremental liveness: the engine returns null on ANY IR edit
  (spill now; split later) and the pass's existing per-round
  `LiveIntervalsAnalysis` rebuilds everything. No `transferValues` needed.
- An unassignable vreg is given exactly one deferral (New→Split, requeued in the
  same Run), then one split attempt (stub), then spills — so one Run terminates,
  and the pass's `maxRounds` cap bounds the outer loop.

**Known Stage-0 limitation (by design, NOT a bug — fix is Stage 1)**
Greedy without eviction cannot resolve a *converging* forced spill the way the
colourer does: it spills the value that failed to assign, not the cheaper victim
the colourer's optimistic-spill cost model picks. So a converging store/reload
spill that needs the right victim selection requires the eviction rung. Captured
in the test `TwoValuesNeedingSameSingleRegSimultaneously_ReachesSpillAndFails`
(asserts a clean non-convergence error, not a hang). Until Stage 1, do NOT flip
the default — any function that actually needs spilling may fail to converge under
greedy.

### Stage 1 — DONE (2026-06-21)

`GreedyRegisterAllocator.TryEvict` fleshed out: weight-based eviction with cascade
loop-prevention. Still flag-gated (`useGreedy`); default path untouched. All green:
Irie 189/189 (188 + 1 new eviction test), `irie-report` unchanged at **-6.7%**
(greedy not on any default path).

**Edits (all in `src/Irie/Passes/GreedyRegisterAllocator.cs`)**
- `TryEvict(vreg, queue)` — for each candidate physreg in preference order (so the
  copy-hint reg wins ties), gather the interfering committed occupants; the
  candidate is usable iff EVERY interferer is evictable: spillable (not in
  `_unspillable`), strictly lighter `SpillWeight`, and an older/absent cascade.
  Pick the min-summed-weight candidate, stamp the evictor's cascade on each
  evictee, `Uncommit` them, and re-enqueue (stage preserved). Mirrors
  RegAllocGreedy.cpp `tryEvict`/`evictInterference` + the advisor's
  `canEvictInterferenceBasedOnCost`.
- Cascade state (`_cascade` + `_nextCascade`, Run-local): `AssignCascade` (lazy,
  on first evict) / `CascadeOf` / `CascadeOrNext`. A value a vreg evicts inherits
  the evictor's cascade, so it can only be evicted back by a STRICTLY newer cascade
  — breaks A↔B cycles (RegAllocGreedy.h `Cascade`, getOrAssignNewCascade).
- `SpillWeight(vreg)` (memoized): Σ block-frequency over each def/use reference ÷
  interval size; unspillable → +∞. LLVM VirtRegAuxInfo shape.
- `ComputeBlockFrequencies` = 10^loopDepth; `ComputeLoopDepths` via natural loops
  (iterative DFS finds back-edges to grey nodes, `NaturalLoopBody` marks each loop
  body). Flagged in-code as a COARSE proxy for BlockFrequencyInfo (no profile data).

**New test** `GreedyRegisterAllocatorTests.ConstrainedValueEvictsLighterFlexibleValue_BothPlaced`
— a long flexible value grabs $a via hint, a short `ac`-pinned value evicts it, the
evictee relocates to zp. The converging forced-conflict the Stage-0 stub couldn't
resolve.

**Not done (deliberately deferred):** real-function/`irie-report` quality
measurement of greedy needs a temporary default-flip, which Stage 4 owns (and the
golden trap warns against standalone `iriec` measurement). Splitting is still a
stub, so branchy functions that need a split spill rather than converge — that is
Stage 2's job, not a Stage-1 regression.

### Stage 2a — DONE (2026-06-22): SplitEditor + instruction-split kind

The `SplitEditor` analogue landed and `GreedyRegisterAllocator.TrySplit` now
performs a real **instruction split** (split-to-register) instead of the Stage-0
stub. Still flag-gated (`useGreedy`); the colourer (default) path is byte-
identical — only a new file plus threading a `greedySplitProducts` set into the
greedy branch. All green: Irie 190/190 (189 + 1 new split test),
DotNetro.Compiler 21/21. `irie-report` unchanged at **-6.7%** (greedy off the
default path by construction).

**New file** `src/Irie/Passes/SplitEditor.cs` — the mechanical core (SplitKit
`SplitEditor` analogue), drastically simplified: it performs only the IR EDIT
(mint a vreg, insert boundary `pseudo.copy`s, rewrite in-region uses) and returns
true; the pass's existing per-round `LiveIntervalsAnalysis` reconstructs liveness
(no incremental `transferValues`). `TryInstructionSplit(vreg, splitProducts)` is
the first split kind — a faithful port of the pass's `TrySplitToRegister`: for a
copy-defined value consumed at narrowing single-physreg uses (operand class a
strict subset of the flexible class, e.g. `axy ⊊ any8`), it mints a flexible temp
at each narrowing use and copies the value into it, so the value widens to the
flexible class (zero-page file) and only the short per-use temps need the scarce
register.

**Edits**
- `GreedyRegisterAllocator` — ctor gained `ISet<int> splitProducts` (pass-owned,
  persists across rounds so a reconciliation temp is never re-split); `TrySplit`
  now `=> new SplitEditor(...).TryInstructionSplit(vreg, _splitProducts)`.
- `RegisterAllocatorPass` — threads a persistent `greedySplitProducts` set into
  the greedy ctor (alongside `greedyStages`). Colourer branch untouched.
- `GreedyRegisterAllocatorTests` — new
  `AxyCliqueExceedingRegisters_InstructionSplitsToZeroPage`: four copy-defined
  values each stored via an `axy`-narrowing `mos6502.st.abs`, all simultaneously
  live → a 4-clique over a 3-register class that assign+evict cannot resolve
  (equal weights). Greedy reaches TrySplit, instruction-splits one value to zero
  page; allocation converges (the Stage-0 stub would throw).

**Duplication note (resolves in Stage 4):** the instruction-split logic now
exists in BOTH `SplitEditor.TryInstructionSplit` (greedy) and the pass's
`TrySplitToRegister` (colourer spill path). Same discipline as the duplicated
`RegisterAllocationSupport.ComputeAllowedColours`: the colourer is the default
during bring-up and is left untouched to avoid risk. They converge when the
colourer is retired in Stage 4.

### Stage 2b — DONE (2026-06-22): local split

`SplitEditor.TryLocalSplit` landed with the CORRECT contention model and is wired
into `GreedyRegisterAllocator.TrySplit` as the second split kind (after
instruction split). Still flag-gated; colourer path byte-identical. All green:
Irie 191/191 (190 + 1 new local-split test), DotNetro.Compiler 21/21;
`irie-report` unchanged at **-6.7%** (greedy off the default path).

The model is the one the earlier half-baked attempt got wrong: local split exists
for a value whose WHOLE range fits no single register (so `TryAssign` already
failed) but whose pieces can each take a DIFFERENT free register. `TryLocalSplit`:
single-block only; needs ≥2 in-block uses; bails if any register is free across
the whole `[lo, hi)` range (then assign should have taken it — splitting buys
nothing); otherwise walks the interior use boundaries and cuts at the first one
where some register is free across `[lo, split)` AND some register is free across
`[split, hi)` (`split` = the def sub-slot just after `use[g]`'s read). The cut
mints a fresh temp in the value's class, copies the value into it right after the
split-point use, and rewrites the later uses to the temp; the next reanalysis
colours the two pieces independently. Termination: each split strictly reduces the
per-range use count, so repeated splitting bottoms out.

The "is register R free across `[start, end)`?" test is supplied by the greedy
allocator as a callback (`RegisterBusyAcross`) over its committed-assignment map
(`_assignedTo`) + the precoloured physreg windows (`PhysRegIntervals`) — only the
allocator has that LiveRegMatrix picture; the SplitEditor cannot derive it from
`LiveIntervals` alone.

**New test** `ValueFitsNoSingleRegButHalvesDo_LocalSplitsInsteadOfSpilling`: an
`axy` value with $x busy across its first half, $a across its second, $y
throughout (three precoloured physreg windows), def by `arith.addi` (non-copy,
non-rematerializable). assign/evict/instruction-split all fail; local split cuts
at the interior store boundary; reanalysis colours the halves to $a and $x. The
`pseudo.spill`-count == 0 assertion is the discriminator — without local split the
value genuinely cannot assign and the store/reload fallback would emit a spill.

### Stage 3 — DONE (2026-06-22): split-around-call

`SplitEditor.TrySplitAroundCall` landed and is wired into
`GreedyRegisterAllocator.TrySplit` as the third/last split kind (after instruction
split and local split). Still flag-gated (`useGreedy`); colourer (default) path
byte-identical. All green: Irie 192/192 (191 + 1 new test), DotNetro.Compiler
21/21; no goldens changed (greedy off the default path).

The case: a value live ACROSS a call whose class is entirely caller-saved (e.g.
`axy`). The call carries the caller-saved registers as explicit implicit-defs (the
clobber barrier `CallLowering` attaches to the `jsr`), so they appear in
`PhysRegIntervals` as precoloured busy windows covering the call's def-point — a
value live across that point overlaps every allowed register there, so assign /
evict / instruction-split / local-split all fail. The fix mirrors llvm-mos
exactly (`sta __rc20` before / `lda __rc20` after): mint a relocation temp in the
**flexible** class, copy value→temp right before the call, re-define value←temp
right after. Reanalysis sees the temp live across the call, so its interference
with the caller-saved clobber windows leaves only the **callee-saved** registers
free — `TryAssign` homes it there with no dedicated callee-saved class needed
(the clobber windows do the constraining, as llvm-mos inflates to the largest
legal superclass and lets interference pick); `PrologueEpilogueInsertionPass`
emits the save/restore. Single contiguous across-call region only — no edge
bundles. Termination: the source's range is shortened to end at the pre-call copy
(no longer crosses the call, so `FindAcrossCallBarrier` won't re-pick it) and the
temp is recorded in `_splitProducts`.

`FindAcrossCallBarrier` recognises a call **structurally** (any instruction with
≥1 implicit-def physreg operand) — no call-opcode coupling. Refs:
`RegAllocGreedy.cpp` `tryRegionSplit` (1202), but the single-block contiguous
case here is a targeted relocate, not full region split.

**New test** `GreedyRegisterAllocatorTests.ValueLiveAcrossCall_SplitsIntoCalleeSavedRegister`
— an `axy` value def'd by non-copy `arith.addi`, single post-call use, live across
a `jsr.abs` clobbering the full caller-saved set. assign/evict/instruction-split
(non-copy def) / local-split (<2 uses) all fail; split-around-call relocates it.
Discriminators vs. a plain spill: (1) some instr defines a callee-saved reg
(RC20..31); (2) zero `pseudo.spill` ops; (3) the post-call store reads a real GPR.

**Not done (deferred to Stage 4):** real corpus / `irie-report` measurement of the
`live-across-call` case needs the default flip (greedy is off the default path by
construction), and the golden trap warns against standalone `iriec` measurement.

### Resume checklist for the next session

1. Read this file + `[[project_greedy_ra_plan]]` memory.
2. Do **Stage 4 — flip the default + delete the manual funnels** (and the
   colourer, `InsertRelocationCopiesForConstrainedDefs`, and the duplicated
   instruction-split in `RegisterAllocatorPass.TrySplitToRegister`). Regenerate
   goldens the harness way.
3. Determinism: keep every queue/tie break on ascending vreg id.

## Risks & mitigations

- **Termination.** The `LiveRangeStage` ladder bounds splitting (a split product
  starts at RS_Split → at most one more split → RS_Spill → RS_Done); spill temps
  are marked unspillable (already done). Keep the existing `maxRounds` safety cap.
- **Determinism.** Every queue/tie must break by ascending vreg id (the
  characterization lit tests depend on stable output) — same discipline the
  colourer already follows.
- **Spill-weight without block frequency.** Loop-depth-from-back-edges is a
  coarse proxy; acceptable for the corpus. Flag in code as a known approximation.
- **Golden regeneration trap.** Confirmed live this session: generate/verify
  goldens under `dotnet test`, never a one-off `iriec`/`irie-report` run.
- **Scope creep toward full LLVM greedy.** The staged plan ships value at each
  step; stop at Stage 4 unless the corpus proves Stage 5 is needed (CLAUDE.md:
  don't build speculative machinery).

## Validation (every stage)

```
dotnet test --project src/Irie.Tests                 # MIR + asm lit + RA unit tests
dotnet test --project src/DotNetro.Compiler.Tests    # end-to-end emulator tests
dotnet run -c Release --project src/Irie.Tools.Reference   # irie-report headline
```

Regenerate touched goldens the harness way; confirm headline ≥ -6.7% and no
per-case regression before advancing.

## Reference anchors (read before each allocator-design step, per CLAUDE.md)

- `llvm/lib/CodeGen/RegAllocGreedy.cpp`: `selectOrSplitImpl` (2651), `tryAssign`
  (537), `tryEvict` (718), `trySplit` (1972), `tryInstructionSplit` (1587),
  `tryLocalSplit`/`calcGapWeights` (1663/1741), `tryRegionSplit` (1202).
- `llvm/lib/CodeGen/SplitKit.{h,cpp}`: `SplitEditor` API
  (`openIntv`/`enterIntvBefore`/`leaveIntvAfter`/`useIntv`/`finish`, 462–521).
- `llvm/lib/Target/MOS/`: `MOSFrameLowering.cpp`, `MOSStaticStackAlloc.cpp`
  (Stage 5 memory-spill placement).
