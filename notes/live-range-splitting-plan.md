# Plan: live-range splitting (eliminate hand-coded "funnel" copies in isel)

Strategic follow-up to the compare-ladder funnel removal (see *Background*). That
change was item (1) — a symptom fix. This note is item (2) — the disease.

## Background: why this note exists

Irie's `MOS6502InstructionSelector` repeatedly hits a recurring pattern where a
selection method must decide, per instruction, whether to **funnel** an operand
through a fresh `Anyi8` vreg before using it. The stated reason (verbatim from
the old `EmitMultiByteCmpLadder` comment) was:

> Funnel each `a` byte through a fresh Anyi8 vreg to break the ABI copy hints
> (byte 0 → $a, etc.) so the RA can park the bytes in zero page across the whole
> chain rather than starving the per-byte $a funnel.

That funnel is a **manual live-range split**: isel detaches a value from its `$a`
affinity by inserting a copy, so the register allocator parks it in zero page.
The funnel-removal change (item 1) deleted the *defensive over-funneling* in the
multi-byte compare ladder — emit one class-constraint copy and trust the
coalescer, exactly as the single-byte path (`EmitI8CmpAndBranch`) already did.
That alone moved the `irie-report` headline from **-1.2% to -6.7%** (if-else
+4→-2, fibonacci +6→+1, loop-counter -19→-27, no regressions).

But the residual concern the funnel was *invented* for is real: when a value
needs `$a` briefly **and** is live across a region, leaving it broad can over-
constrain the colourer. Item (1) works because in low-pressure cases the
coalescer cleans up; it does not give us what llvm-mos has in high-pressure
cases.

## The root cause

Irie's register allocator (`src/Irie/Passes/GraphColouringAllocator.cs`) is
textbook **Appel iterated register coalescing** — Build / MakeWorklist /
Simplify / Coalesce / Freeze / SelectSpill / AssignColors (Appel, *Modern
Compiler Implementation*, ch. 11). That algorithm has a structural property:

> **A node is coloured whole or spilled whole. It cannot split a live range.**

So a value cannot live in `$a` for part of its life and in zero page for the
rest. When `$a` pressure across a value's live range would make it uncolourable,
classic graph colouring's only lever is to **spill the entire range** (Irie's
`SpillVregs`: rematerialize, or store/reload at every def/use).

llvm-mos (and LLVM generally) is architecturally different. Its passes, in order:

| Pass | File | Role |
|------|------|------|
| Instruction selection | `MOSInstructionSelector.cpp` | emits generic `COPY` + register-class-constrained vregs; **no funnel decisions** |
| Register coalescer | `llvm/lib/CodeGen/RegisterCoalescer.cpp` | removes the redundant copies isel emitted naively |
| Two-address pass | `TwoAddressInstructionPass.cpp` | inserts the preserving copy for tied/destructive ops (generic, not per-instruction) |
| Greedy RA + splitting | `RegAllocGreedy.cpp` + **`SplitKit.cpp`** | **splits live ranges** so part lives in a reg and part is spilled/relocated, inserting moves *only where pressure demands* |
| Copy propagation | `MachineCopyPropagation.cpp` | final cleanup |

The key llvm-mos isel does **not** reason about `$a` contention or hint-breaking.
`SplitKit` does the splitting at allocation time, where the actual pressure is
known. Irie has the coalescer (good) and spilling (whole-range only) but **no
live-range splitting**, so the splitting decision leaks *upstream* into isel as
hand-coded funnels — and the burden scales with the opcode set.

## What "done" looks like

isel becomes consistently dumb-and-trusting: it emits class-constrained vregs
and generic `pseudo.copy`s and **never** funnels for RA reasons. Any value that
needs a register briefly but is live across a region is handled by the allocator
splitting its range, not by an isel-inserted copy. The remaining gap vs llvm-mos
then becomes a clean measurement of what splitting buys, rather than being masked
by hand-tuning.

## Options

### Option A — graph colouring + bolt-on splitting (incremental)

Keep the Appel core; add a splitting step to the spill path. Classic references:
Cooper & Dasgupta "live range splitting", or the simpler **passive splitting**:
when a node would spill, instead of store/reload at every use, split its live
range at high-pressure region boundaries (e.g. around a call, or around the
sub-range where `$a` is contended), creating two shorter ranges joined by a copy,
and re-run colouring. This is closest to what the funnels were faking.

- **Pros:** reuses the existing allocator; smaller blast radius; can be staged
  (start with split-around-calls for `live-across-call`, then generalize).
- **Cons:** graph colouring resists splitting — the interference graph must be
  rebuilt after each split; convergence/termination needs care; the heuristic
  for *where* to split is the hard part and is exactly what `SplitKit` spent
  years tuning. Risk of trading one set of hand-tuning for another.

### Option B — greedy allocator with a SplitKit analogue (rewrite)

Replace the colouring core with an LLVM-style greedy allocator: priority-ordered
allocation, live-range splitting via a `SplitKit`-style "split around region /
split at boundary" engine, eviction, and spilling as the last resort.

- **Pros:** this is the design proven *on this exact target* (llvm-mos); makes
  the funnels disappear by construction; matches the reference we already anchor
  to per CLAUDE.md, so the corpus directly validates it.
- **Cons:** large; `SplitKit` is one of the more intricate parts of LLVM
  codegen; needs live-range/segment infrastructure Irie doesn't have yet
  (`LiveIntervals` exists but not the segment-splitting machinery).

## Recommendation

Stage it, leading with the cheap symptom sweep and only then deciding A vs B from
data:

1. **Finish the symptom sweep first (cheap, do opportunistically).** Audit the
   remaining `FunnelThrough*` / defensive class-pinning copies in
   `MOS6502InstructionSelector.cs` and apply the same "emit one class-constraint
   copy, trust the coalescer" treatment the compare ladder and i8 path now use.
   Each one is independently shippable and measured by `irie-report`. This both
   buys real wins and **shrinks the set of cases that actually need splitting**,
   so the eventual A-vs-B decision is made against the true residual.

2. **Measure the residual.** With isel dumb, the converted cases still losing to
   llvm-mos are (almost certainly) the genuine high-pressure / live-across-call
   ones — `pressure/*`, `realistic/factorial-recursive`, `pressure/many-calls`
   (currently *blocked*, but they are the splitting stressors). Pair a few of
   these and read where Irie's whole-range spill loses to llvm-mos's split.

3. **Pick A or B from that data.** If the residual is dominated by a *narrow*
   pattern (e.g. split-around-call only), Option A's passive splitting may close
   it cheaply. If it is broad (pervasive `$a`/`$x`/`$y` contention across
   regions), bite the bullet on Option B. Do **not** commit to B speculatively —
   the funnel sweep may reveal the residual is small enough that A suffices.

## Outcome (2026-06-21)

Steps 1–3 executed.

**Step 1 — sweep already complete.** The removable funnels were the *input*
funnels to the non-destructive `cmp` ladder; commit b976486 already deleted
them (headline → -6.7%). Every remaining funnel in `MOS6502InstructionSelector.cs`
is the *same structural idiom*: a value **forced into `$a`** by the op (def tied
to `$a`, or load result lands in `$a`) then copied out to a broad `Anyi8` vreg —
a **manual live-range split**, not defensive over-funneling. Verified
empirically: removing the adc/sbc out-funnel made `common-subexpr` and
`early-return` **spill** (`pseudo.spill` survived to `PseudoExpansionPass` and
crashed — graph-colouring spills whole ranges, memory-spill lowering isn't on the
asm path). The Release `irie-report` showed no change (stale-build trap, per the
CLAUDE.md golden warning); the Debug lit suite told the truth. No free wins left
in isel.

**Step 2 — residual is narrow.** Converted cases where Irie loses:

| Case | Δ | Nature | Splitting? |
|------|---|--------|-----------|
| control-flow/common-subexpr | +3 | `HoistCommonCodePass` too narrow; emits `a+b` twice | ❌ hoist/GVN gap |
| pressure/live-across-call | +1 | `a` live across call; llvm-mos relocates to callee-saved `__rc16` + stack save/restore | ✅ split-around-call |
| control-flow/early-return | +1 | guard-clause short paths; prompt register freeing | ✅ split granularity |
| realistic/fibonacci | +1 | rotate-in-loop copy cycle, back-edge liveness | ⚠️ mostly coalescing |

Genuine RA-splitting residual = `live-across-call` + `early-return`. The one
genuine-spill blocked stressor `pressure/many-calls` is blocked on
**memory-spill lowering not wired to `--emit=asm`**, not on missing splitting;
`pressure-high`/`factorial-recursive` are blocked on **missing multiply**. No
broad cross-region contention is measurable. Irie already has an instruction-split
primitive (`RegisterAllocatorPass.TrySplitToRegister`, the llvm-mos greedy
`tryInstructionSplit` analogue); it lacks **region splitting** (around-call /
at-boundary).

**Step 3 — decision: Option B** (greedy allocator + SplitKit analogue). The data
favoured A (narrow residual, instruction-split primitive already present), but the
maintainer chose B: the design proven on this exact target, making the manual
funnels disappear by construction and matching the llvm-mos reference the corpus
validates against. Implementation plan follows in
[`notes/greedy-ra-splitkit-plan.md`](greedy-ra-splitkit-plan.md).

## Concrete first steps (when this is picked up)

- Grep `MOS6502InstructionSelector.cs` for fresh-vreg-then-copy idioms that exist
  to "break a hint" or "park in zp" rather than to satisfy a tied/class
  constraint. Candidate: the `ReclassifyTo(..., Anyi8)` funnels around the
  ADC/SBC/flag paths (lines ~315, ~408, ~458, ~1540 at time of writing).
- For each, remove the funnel, rebuild Release `iriec`, regenerate the affected
  lit goldens (produce them the *harness* way — run under `dotnet test`, per
  CLAUDE.md's golden-regeneration warning), re-run `irie-report`, and confirm the
  headline does not regress before moving on.
- Read `MOSInstructionSelector.cpp` and `SplitKit.cpp` (under
  `/Users/timjones/Code/llvm-mos`) before designing the allocator change, per the
  two-sources rule in CLAUDE.md.

## Why not just keep funneling

It works, but it puts RA-anticipation logic in every selection method: each new
opcode whose operand is class-pinned *and* outlives the instruction forces the
author to reason about `$a` contention by hand. That is the recurring pain that
prompted this note. The funnel is the allocator's job, done in the wrong pass.
