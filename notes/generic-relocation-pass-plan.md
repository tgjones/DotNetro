# Plan: generic relocation pass → single greedy allocator

A focused, self-contained plan. It supersedes and replaces four earlier notes
(live-range-splitting, greedy-ra-splitkit, splitter-spiller-rework + its progress
log); everything load-bearing from those is restated here, and the detailed
greedy characterization they recorded now lives in code comments and the commit
messages for `d7fcaf4` / `48fdd11` / `e12fe34`.

## What DotNetro / Irie is (one paragraph)

DotNetro compiles .NET IL to 6502 assembly. The **Irie** backend (`src/Irie`) is
the modern path: a single MIR flows through instruction selection
(`Target/MOS6502/MOS6502InstructionSelector.cs`), then register allocation
(`Passes/RegisterAllocatorPass.cs`), then target lowering. The 6502 codegen is
anchored to **llvm-mos** as the reference (sources + a local toolchain under
`/Users/timjones/Code/llvm-mos`; per CLAUDE.md, design questions are answered
from both the reference's *output* and its *source*). Quality is measured against
a 46-case corpus by `irie-report` (`dotnet run -c Release --project
src/Irie.Tools.Reference`, scoreboard at `doc/irie/llvm-mos-comparison.md`,
inputs under `ext/llvm-mos-reference`).

## The problem: per-opcode "funnels" in instruction selection

The 6502 forces many results into a single physical register: a `mos6502.adc`
result is tied to `$a`, an `lda` result lands in `$a`, etc. If that value is
still needed after the next instruction re-defines `$a`, it must be **moved off
`$a`** first (there is one `$a` and two live values wanting it). Today
`MOS6502InstructionSelector.cs` does this with **seven hand-coded "out-funnels"**:
the selector mints a fresh flexible (`Anyi8`) vreg, copies the constrained result
into it, and redirects later uses — a manual live-range split, performed in
instruction selection. The seven sites (line numbers drift — grep `out-funnel`):

- **arithmetic (4):** plain `adc`/`sbc`, folded-abs `adc`/`sbc`, folded-imm
  `adc`/`sbc`, and the EOR funnel (signed-compare bias byte).
- **loads (3):** frame-load, `lda.abs`, `lda.indy`.

**Why this is debt:** every new opcode whose result is class-pinned *and* outlives
its instruction forces the author to hand-reason about `$a` contention. That is
register-allocation logic leaking upstream into selection, replicated per opcode.

**The key distinction this plan turns on:** the *relocation copy itself* is
legitimate and necessary — it is exactly the pre-RA split llvm-mos's isel emits
(isel emits generic `COPY`s around physreg-constrained operands; the coalescer
folds the free ones, the splitter places the contended ones). What is debt is the
**per-opcode hand-coding** of *where* and *whether* to emit it. So: keep the copy,
kill the hand-coding — by emitting it from **one generic pass** instead of seven
bespoke selector branches.

## Where we are now (committed state)

- **Two allocators exist** in `Passes/`, selected by `RegisterAllocatorPass(useGreedy:)`:
  - the **graph-colouring** allocator (`GraphColouringAllocator.cs`, Appel
    iterated coalescing) — **the current default**. It cannot split a live range
    (a node is coloured whole or spilled whole), so it leans entirely on the
    funnels + the relocation pass below to pre-split contended values; strip them
    and it fails outright (`pseudo.spill` survives to a crash — memory-spill
    lowering is not wired to the asm path).
  - the **greedy** allocator (`GreedyRegisterAllocator.cs` + `SplitEditor.cs`,
    LLVM-style assign→evict→split→spill) — **flag-gated, not default**. Built and
    validated to converge with zero spills on the corpus and to emit
    **byte-identical to llvm-mos** on straight-line carry chains
    (`add_i16`/`sub_i16` → `clc/adc/tay/txa/adc/tax/tya/rts`). Its remaining gap
    is cross-block *placement* (see C).
- **`InsertRelocationCopiesForConstrainedDefs`** (`RegisterAllocatorPass.cs` ~L589)
  already runs on **both** paths every allocation. It is a generic relocation
  pass — but only for **tied defs** (`adc`/`sbc` results) and only **block-local**
  (it redirects later *in-block* uses, not cross-block ones). Its own header calls
  it "the surviving, genuinely-load-bearing remnant" of result-preservation. **It
  is ~80% of the generic pass this plan wants.**
- A gated RA trace (`RaTrace.cs`, env `IRIE_RA_TRACE`, stderr-only, off by
  default) is available for diagnosing allocation round-by-round.

## End goal (the thing every step serves)

**One register allocator — greedy — and a dumb instruction selector.** The
selector emits class-constrained vregs and generic copies with *no* funnel logic;
a single generic pass materializes the constrained-result relocation copies; the
allocator coalesces the free ones and places/splits the contended ones; the
graph-colouring allocator is **deleted**. We do not want two allocators in the
tree — the colourer is retained only transiently, as a validation oracle, and is
removed at the end of C.

## Plan B — the focused first step: one generic relocation pass

**Replace the seven per-opcode funnels with one generic relocation pass**
(generalizing `InsertRelocationCopiesForConstrainedDefs`), validated on the
colourer-as-oracle, with greedy left flag-gated. This pays down the isel debt now
and lays the substrate greedy needs for C — and it should *simplify* greedy,
because once the relocation copy is in the IR up front, greedy no longer has to
*discover and insert* it during allocation (the fragile part); it only has to
coalesce or place it.

### Facts the implementation depends on (so no exploration is needed mid-flight)

1. **The abs-load fold is coupled to one funnel (gap-1).** `TryMatchFoldableAbsLoad`
   (`MOS6502InstructionSelector.cs` ~L362) matches the exact chain
   `adc-operand → pseudo.copy → lda.abs @sym` and *consumes the copy*, rewriting
   to `adc.abs @sym`. Removing the `lda.abs` funnel deletes that middle copy, the
   matcher fails, and the fold silently stops firing (dead `lda.abs` + bloat:
   `global-rw` 10→18, `MemGlobalAdcFold` loses its fold). **Fix:** make the
   matcher walk to the operand's def directly and accept *either* the
   `pseudo.copy → lda.abs` shape *or* a direct `lda.abs` def — exactly llvm-mos's
   `m_FoldedLdAbs` (`getOpcodeDef(G_LOAD_ABS)`, no copy in the pattern). ~15 lines,
   prototyped and confirmed to restore the fold both ways; `lda.abs`'s result has
   no dialect-imposed operand class so it is freely reclassifiable. The **EOR**
   funnel and the frame/indy load funnels are *not* fold-matched — only `lda.abs`
   is.
2. **Cross-block redirect is what the funnels do that the current pass doesn't.**
   A funnel runs in isel, before the value flows to successor blocks, and renames
   *all* uses (cross-block included). The current relocation pass only redirects
   in-block uses, so simply deleting a funnel that feeds a cross-block value would
   regress it. The generic pass must redirect **all** uses after the relocation
   point across **all** blocks. Because it runs once (not per allocation round) on
   pre-RA SSA-shaped MIR, this is a single use-list walk + rename — the same
   cross-block-redirect move already proven in `SplitEditor` (commit `48fdd11`),
   but simpler. The colourer's global coalescer then places the single resulting
   cross-block vreg coherently in one register (this is precisely why the funnels
   work for the colourer today).
3. **Each funnel pins its result differently** — confirm per site before
   generalizing: `adc`/`sbc` results are tied-def pinned to the `Ac` operand class
   (already covered by the pass); `lda`/load and EOR results are pinned by
   instruction semantics (the op writes `$a`) rather than a def operand class. The
   generic pass's trigger must therefore be "result occupies a single-physreg
   class **or** is produced by an op that writes a single physreg," not just
   "tied def." Verify the exact predicate against `MOS6502InstructionInfo` /
   `MOS6502Dialect`.
4. **Golden discipline (CLAUDE.md, load-bearing).** Keep `CHECK` blocks **strict**
   (complete listings, one `CHECK` per output line) — never loosen a block to make
   it pass. **Regenerate goldens the harness way:** produce/verify them under
   `dotnet test`, *not* a one-off standalone `iriec` run. Three frame-slot cases
   (`AddressTakenFrameSlot`, `StaticFrameAllocChain`, `FrameSlotStructRoundtrip`)
   emit a different-but-valid form standalone vs. under the harness — **pin them to
   the harness form**, do not chase the standalone form.
5. **Determinism:** every queue / tie-break on ascending vreg id.

### Staged implementation (each stage independently committable + green)

- **B0 — decouple the abs-load fold (gap-1).** Apply the matcher fix above with
  *all funnels still present*. Goldens must be **unchanged** (the fold still fires
  via the existing funnel shape). Commit. This unblocks removing the `lda.abs`
  funnel later without losing the fold.

- **B1 — cross-block redirect + remove the arithmetic funnels.** Add cross-block
  use redirection to `InsertRelocationCopiesForConstrainedDefs` (fact 2). Then
  delete the four arithmetic funnels (plain/folded-abs/folded-imm `adc`/`sbc` +
  EOR). The generic pass now covers their results, cross-block included. Validate:
  goldens **equal-or-better** the harness way; both suites green; `irie-report`
  headline held. Commit.

- **B2 — cover load results + remove the load funnels.** Extend the generic pass's
  trigger to single-physreg-class load/EOR-style results (fact 3). Delete the
  three load funnels (relies on B0 for the abs-load fold). Validate as B1. Commit.

- **B3 — measure & decide C.** Run `irie-report`; record the headline and the
  per-case residual where the colourer (now funnel-free) still loses to llvm-mos.
  This is the measurement the earlier effort kept skipping. It tells us how much
  cross-block *placement* (C) actually buys before we commit to building it.

### Validation (every B stage)

```
dotnet test --project src/Irie.Tests                 # unit + lit
dotnet test --project src/DotNetro.Compiler.Tests    # emulator (behaviour, not just shape)
dotnet run -c Release --project src/Irie.Tools.Reference -- generate-report   # irie-report headline
```
Default stays the **colourer** throughout B (the validation oracle). Greedy stays
flag-gated; B should leave greedy *simpler* (the up-front copy obviates greedy's
constrained-def-result / incumbent split-insertion kinds — verify and remove any
that go dead, but that cleanup can wait for C). The RA trace stays.

## C — probable follow-on (NOT committed until B's results are in)

The end goal is greedy as the **sole** allocator. After B, the only thing
standing between greedy and that is **cross-block placement**: greedy already
inserts the cross-block relocation copy (commit `48fdd11`) and matches llvm-mos on
straight-line chains, but on cross-block ranges it currently places the relocated
value in **memory** (per-block `STA`/`LDA`) rather than holding it in one register
across blocks (`early-return` 29→31, `loop-counter` 38→40, `fibonacci` 51→55 under
greedy-with-funnels-off). The colourer only wins those via its **global**
coalescer; llvm-mos wins them via `tryRegionSplit` (EdgeBundles + SpillPlacement).

So C is: **give greedy coherent cross-block placement** of a relocated value —
the region-split-lite that holds one flexible vreg in one register across an
acyclic multi-block range (sized at ~150–250 lines in prior investigation). With
B's generic copy already in the IR, C's job narrows to *placement*, not
discovery. Then: flip `useGreedy` to default, **delete the colourer** +
`ComputeAllowedColours`/`TrySplitToRegister` colourer-only support + any greedy
split-insertion kinds B made dead, regenerate goldens, confirm `irie-report`
holds, and the tree converges to **one allocator**.

**Why C is described but not committed:** loop-crossing cases (`loop-counter`,
`fibonacci`) want true region split, which earlier work deliberately scoped out as
intricate (EdgeBundles/SpillPlacement/InterferenceCache). B3's measured residual
decides whether C's acyclic placement is enough, whether the loop cases justify
real region split, or whether the residual is narrow enough to accept. Do not
build region split speculatively — let B's data set C's scope.

## Risks

- **Over-relocation churn.** The generic pass may emit a copy the funnels didn't;
  the coalescer should fold it, but watch for stragglers in the goldens (B1/B2
  catch them, equal-or-better gate).
- **Cross-block redirect correctness.** Renaming cross-block uses must respect
  re-definitions (stop at the next def of the value); reuse the `SplitEditor`
  logic and unit-test the two-block shape.
- **Golden-regeneration trap.** Regenerate/verify under `dotnet test`; pin the
  three frame-slot nondeterminism cases to the harness form (fact 4).
- **Two-allocator interlude.** The colourer lives through B and into C as the
  oracle — acceptable *because it is on death row*, deleted the moment greedy
  supersedes it. The end state has one allocator.
