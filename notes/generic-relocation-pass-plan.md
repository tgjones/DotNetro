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

## Where we are now (committed state, refreshed 2026-06-25)

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
    (`add_i16`/`sub_i16` → `clc/adc/tay/txa/adc/tax/tya/rts`). It already does its
    own live-range splitting here; its remaining gap is cross-block *placement*
    (see C).
- **Funnel removal progress (B0/B1 + constrain-operands port):**
  - **B0** done (`1a8f945`): abs-load fold decoupled from the `lda.abs` funnel.
  - **B1** done (`4ad8e3f`): the 4 **arithmetic** funnels removed;
    `InsertRelocationCopiesForConstrainedDefs` now does slot-precise **cross-block**
    redirect (no longer block-local).
  - The **EOR** funnel is also gone: `eor.imm`'s result is a **tied def**
    ([`MOS6502InstructionSelector.cs` `EorByteWithImmediate`]), so the relocation
    pass's existing tied-def trigger already covers it. **5 of 7 funnels retired.**
  - The arith/cmp paths were further cleaned by the llvm-faithful
    `RegisterClassConstraining.ConstrainSelectedInstRegOperands` port
    (`8e953bf`/`dd9151e`, the `constrainSelectedInstRegOperands` mirror; −8.4%).
  - **Still live: the 3 load funnels** — `frame.load` ([~L1106]), `lda.abs`
    ([~L1142]), `lda.indy` ([~L1162]) — whose `Ac`-class results are **not** tied
    defs, so the relocation pass doesn't cover them. These are what "B2" targets.
- **`InsertRelocationCopiesForConstrainedDefs`** (`RegisterAllocatorPass.cs` ~L599)
  runs on **both** paths every allocation. Triggers on **tied defs** only; does
  cross-block redirect. **See the faithfulness section below before generalizing
  it** — its cross-block live-range split is colourer scaffolding, not an llvm-mos
  pass.
- **Standalone `RegisterCoalescerPass`** landed (`fa28d6d`), faithful llvm-mos
  `RegisterCoalescer` mirror, in the iriec pipeline between TwoAddress and RA,
  **gated off** behind `IRIE_COALESCE=1` (wiring it on for the colourer crashes 43
  lit tests — aggressive joining is only safe with a splitting allocator). This is
  greedy's substrate.
- A gated RA trace (`RaTrace.cs`, env `IRIE_RA_TRACE`, stderr-only, off by
  default) is available for diagnosing allocation round-by-round.

## llvm-mos faithfulness check (2026-06-25) — read before generalizing the relocation pass

Maintainer raised: **does llvm-mos have a standalone pass that materializes
constrained-result relocation copies?** Findings (checked the reference, per
CLAUDE.md):

1. **There IS a standalone post-isel copy-insertion pass:**
   `MOSInsertCopies.cpp` (`createMOSInsertCopiesPass()`, run immediately after
   `InstructionSelect`). **But its job is the opposite of ours.** It fires for a
   *curated* opcode set (`ASL/LSR/ROL/ROR` → `AImag8`; `INC/DEC/…` → `Anyi8`),
   only on **tied** def/use operands, and inserts copies to **widen** the register
   class so the allocator can pick a *faster addressing mode*. It is **local** (a
   use-copy before + a def-copy after the one instruction — `setReg` +
   `buildCopy`), does **no cross-block use rename**, and **pre-decides nothing**:
   it merely *offers* the coalescer/greedy a copy they may fold or keep.
2. **There is NO llvm-mos pass that cross-block-renames a constrained value to
   pre-split its live range.** In llvm, that relocation is the **register
   allocator's** job: isel emits class-constrained vregs (+ the bridging copies
   `constrainSelectedInstRegOperands` inserts for class *mismatches* only), the
   `RegisterCoalescer` folds the free ones, and **`RegAllocGreedy` splits the
   contended ones** (SplitKit/SplitEditor, tryLocal/tryRegion split, cost-driven
   by SpillPlacement/InterferenceCache). Our byte-identical `add_i16` under greedy
   confirms greedy already does exactly this for the `$a` adc-chain contention.

**Conclusion — the maintainer is right.** Our `InsertRelocationCopiesForConstrained-
Defs` *cross-block* behaviour (and the funnels' up-front rename-all-uses) is
**doing greedy's live-range-splitting in a pass**, and exists **only because the
colourer cannot split**. It has no llvm-mos analogue and is **colourer
scaffolding**, not a permanent design. Generalizing it into the load funnels (the
original "B2") would be building *more* non-faithful crutch with **no corpus
coverage to validate it** (see research below).

The *narrow, local, widening* `MOSInsertCopies` analogue is faithful and may be
worth a small targeted pass **later** — but only as an *offer* to the allocator,
not a pre-split, and only if we measure the same addressing-mode benefit.

## Research (2026-06-25): the corpus does not exercise the load funnels

Scanned all 17 `.irie` reference tests (of 46 corpus cases; only 15 paired in
`irie-report`) post-`InstructionSelector`:

- **`lda.abs`: 0**, **`lda.indy`: 0**, **`frame.load`: 0** across every reference
  test. The only load-ish op is `adc.abs` ×2 in `memory/global-rw` — the **fold**
  path (the load is consumed into the ALU op), not the funnel.
- Only 2 of the 17 use memory ops at all: `memory/global-rw` (loads, both folded)
  and `control-flow/common-subexpr` (stores only). `ptr-deref`, `array-index`,
  `two-pointer-copy`, `strlen` etc. have **no `.irie` test**.

The 3 load funnels are instead exercised only by ~10 **hand-authored** MOS6502 lit
goldens: `lda.abs` — `MemGlobalAbsolute`, `MemLoadStoreI8/I16`; `lda.indy` —
`RuntimePointerLoad(Store)`, `WriteLineString`; `frame.load` —
`AddressTakenFrameSlot`, `StaticFrameAllocChain`. So **B2 cannot be measured by
`irie-report`** — its only validation would be those goldens. Diff reports for the
two memory corpus cases written to `artifacts/irie-diff/{memory-global-rw,
control-flow-common-subexpr}.html`.

## End goal (the thing every step serves)

**One register allocator — greedy — and a dumb instruction selector**, matching
llvm-mos's pipeline shape. The selector emits class-constrained vregs and the
class-bridging copies `constrainSelectedInstRegOperands` inserts (no funnel logic);
the `RegisterCoalescer` folds the free copies; **greedy places/splits the contended
values itself** (live-range splitting — *not* a pre-split pass); the graph-colouring
allocator and **all the colourer scaffolding** (the funnels *and*
`InsertRelocationCopiesForConstrainedDefs`) are **deleted together**. We do not want
two allocators, and we do not want a permanent cross-block relocation pass with no
llvm analogue — the colourer + its scaffolding are retained only transiently, as a
validation oracle, and removed at the end of C.

> **Revised 2026-06-25** (was: "a single generic pass materializes the
> constrained-result relocation copies"). That framing is dropped — see the
> faithfulness section. The cross-block relocation copy is greedy's job, not a
> pass's. The only faithful standalone copy-insertion pass in llvm-mos
> (`MOSInsertCopies`) is a narrow *local widening* offer, not a pre-split.

## Plan B — SUPERSEDED for the load funnels (2026-06-25)

> **Status:** B0/B1 (+EOR via tied-def, +constrain-operands port) are **done** and
> were the load-bearing, faithful part — `constrainSelectedInstRegOperands` is a
> real llvm-mos utility. **B2 as originally written — "generalize
> `InsertRelocationCopiesForConstrainedDefs` to cover the 3 load funnels" — is
> dropped.** Two reasons, both from the 2026-06-25 research above: (a) generalizing
> the *cross-block* relocation pass builds more colourer-only scaffolding with no
> llvm-mos analogue (greedy splits; a pass does not); (b) the corpus does not
> exercise the load funnels, so there is **nothing for `irie-report` to validate it
> against**. The original Plan B text is kept below for history; the live path is
> now the **Revised path** section that follows.
>
> The original rationale ("pre-insert the copy so greedy only coalesces/places, not
> discovers") is **the non-faithful claim** the maintainer correctly rejected:
> discovering and placing the split *is* greedy's job in llvm.

### Original Plan B (historical) — replace the seven per-opcode funnels with one generic relocation pass

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

## Revised path (2026-06-25) — to one faithful allocator

The faithfulness check moves the remaining work off "grow the relocation pass" and
onto "finish greedy." The 3 load funnels and `InsertRelocationCopiesForConstrained-
Defs` are **not** removed under the colourer — they die *with* the colourer when
greedy takes over. Order:

1. **C — give greedy coherent cross-block placement** (region-split-lite; see
   below). This is the real blocker to one allocator. Greedy already splits within
   a block (byte-identical adc chains); it lacks cross-block holding of a relocated
   value. Validate with the colourer as oracle + emulator behaviour.
2. **Wire `RegisterCoalescerPass` into the greedy path** (it is the faithful
   substrate; currently gated `IRIE_COALESCE=1`). Greedy + coalescer is the
   llvm-mos shape: coalescer folds free copies, greedy splits contended ones.
3. **Flip `useGreedy` default; delete the colourer and ALL its scaffolding
   together** — the 3 load funnels, `InsertRelocationCopiesForConstrainedDefs`,
   `ComputeAllowedColours`/`TrySplitToRegister`, and any greedy split-insertion
   kinds the coalescer-up-front made dead. Removing the funnels is then *free*:
   greedy splits the `Ac`-class load results exactly as it splits adc results.
   Regenerate goldens the harness way; confirm `irie-report` holds.
4. **(Optional, later, faithful)** if measurement shows the same addressing-mode
   wins llvm-mos gets from `MOSInsertCopies`, add a *narrow local widening* pass
   mirroring it (offer-to-allocator, no cross-block rename). Not a relocation pass.

Net: the only NEW engineering is C. Everything else is wiring + deletion.

## C — cross-block placement (the only remaining engineering) — MEASURED 2026-06-25

### The residual, measured precisely

Ran greedy in its **end-state config** (`useGreedy` on, relocation pass off) on the
cross-block corpus cases (temporary `IRIE_GREEDY` / `IRIE_NO_RELOC` flags, since
reverted). Instruction-line counts (lower = better):

| case | llvm-mos | colourer (default) | greedy end-state |
|---|---|---|---|
| `basics/add-i16`,`add-i32`,`sub-i16` | — | — | **byte-identical to llvm-mos** |
| `control-flow/early-return` | ~29 | 35 | 37 |
| `realistic/fibonacci` | — | 55 | 59 |
| `control-flow/loop-counter` | — | 44 | 46 |

**Two hard facts this nails down:**

1. **Straight-line is solved.** Multi-byte carry chains of *any* width in one block
   — i16 *and i32* — are **byte-identical to llvm-mos** under greedy end-state
   (`clc/adc/tay/txa/adc/tax/lda __rc/.../sta __rc/tya/rts`). In-block splitting +
   the Stage-R2 GPR copy-cost preference already work. **The gap is purely
   cross-block.**

2. **The gap is GPR-vs-zero-page placement, NOT stack spilling.** On `early-return`,
   the colourer holds the running accumulator low byte in **Y** across blocks
   (`TAY` in one block, `TYA` in the next — 1 byte/2 cyc each); greedy parks the
   same value in a **zero-page** register `$06` and reloads it per block
   (`LDA $06`/`STA $06` — 2 bytes/3 cyc each). Y was *free* the whole time —
   greedy chose zp because its `Anyi8` allocation order puts the abundant zp pool
   *ahead* of the 3 scarce GPRs for general (non-split-product) flexible values.
   So the cross-block carrier pays the dearer zp copy cost.

This is exactly the `getRegAllocationHints` copy-cost story already documented in
`GreedyRegisterAllocator.PreferenceOrder` — but currently applied **only to split
products**. A cross-block carrier that is *not* a split product misses it.

### Reference (checked, per CLAUDE.md)

- `RegAllocGreedy.cpp` `trySplit` ladder: `tryLocalSplit` → `tryInstructionSplit`
  (in-block; we have both) → **`tryRegionSplit`** (multi-block; EdgeBundles +
  `SpillPlacement` + `InterferenceCache` — the heavy machinery) → **`tryBlockSplit`**
  (the simple fallback: split around *every* block with uses via
  `splitSingleBlock`, remainder → spill; **no EdgeBundles**).
- `MOSRegisterInfo::getRegAllocationHints` — scores each candidate register by the
  **copy cost** across the value's copies (a carrier escaping `$a` is cheapest in a
  GPR: `$a↔$y` = `tay`/`tya`, 1 byte; dearer in zp: `sta`/`lda`, 2 bytes).
- `MOSInsertCopies.cpp` — the only post-isel standalone copy pass; *local widening*
  offer, not a relocation (see the faithfulness section).

### Staged plan — cheapest faithful lever first

- **C1 — generalize the copy-cost GPR preference (the `getRegAllocationHints`
  analogue). TRIED 2026-06-25 as a binary carrier predicate — NET-NEUTRAL, not
  committed.** Implemented `IsGprCarrier(vreg)` (a flexible value sharing a
  `pseudo.copy` with a GPR-pinned / GPR-assigned / GPR-physreg end) and OR-ed it
  into `PreferenceOrder`'s split-product GPR step. Measured across all 17 reference
  `.irie` cases under greedy end-state (`IRIE_GREEDY`+`IRIE_NO_RELOC`, since
  reverted):

  | case | colourer/llvm | greedy pre-C1 | greedy + C1 |
  |---|---|---|---|
  | `early-return` | 29 | 31 | **30** |
  | `loop-counter` | 40 | 42 | **40** (parity) |
  | `if-else` | 32 | 33 | **35** (worse) |
  | `global-rw` | 10 | 14 | **15** (worse) |
  | (other 13) | — | — | unchanged |
  | **TOTAL (17)** | **315** | **330** | **330** |

  **Verdict: a wash.** The binary preference helps the two cross-block targets
  (−3) but burns a GPR another value wanted on `if-else`/`global-rw` (+3) — exactly
  the GPR-contention the `PreferenceOrder` doc already warns about. A binary
  "prefer GPR if carrier" is too blunt; the faithful `getRegAllocationHints` is a
  **summed copy-cost SCORE per candidate over ALL the value's copies** (prefer a
  GPR only when the *net* savings justify it), which would capture the wins
  without the collateral. That is the real C1 if we pursue it — but see the
  measured headline: greedy is only **+15 / +4.8 %** over the colourer across these
  17, and C1-as-preference does not move the total. **The dominant lever is the
  coalescer, not preference (below).**

- **The standalone coalescer is NOT greedy's lever — it BREAKS greedy. Falsified
  hypothesis, investigated in depth 2026-06-25.** I had assumed wiring
  `RegisterCoalescerPass` into greedy would close the gap (the colourer wins
  cross-block via *its* global coalescer). **It does the opposite — greedy +
  coalescer crashes, and the crash is not a small bug.** Root cause, traced to the
  IR: the aggressive coalescer merges the **arg byte, the two-address accumulator,
  AND the result-that-outlives** into a single multi-def vreg (`add-i16`: `%3` is
  `copy $a`, then `%3 = adc %3(tied), …`, then `$a = copy %3` at the return — three
  roles, one vreg). Greedy's relocation-split kinds are **use-redirect** edits, not
  SlotIndex/segment splits: they cannot separate those roles. Relocating around the
  tied use does not address the result's span across the *next* adc, so the
  well-founded measure never drops and the termination assertion trips
  (`RegisterAllocatorPass.cs` ~L163, "did not reduce … (was N, now N)"). I tried
  three targeted fixes (tied-use copy-back; making `FindLaterReClobber` /
  `SpansClobberOfPinnedReg` agree via liveness instead of a single def-point) — each
  moved the crash to a new vreg/shape. Per CLAUDE.md ("a second error is the signal
  the approach is wrong — back out"), **all reverted; tree clean.** The faithful fix
  is a real **SlotIndex/segment-based SplitKit** (llvm `SplitEditor` proper:
  `openIntv`/`enterIntvBefore`/`leaveIntvAfter`/`useIntv`/`finish`, splitting a
  multi-def range at a precise program point with renaming) — a substantial piece,
  not a patch.

  **Corrected conclusion:** the colourer *needs* the coalescer (it cannot split);
  greedy does **not** — greedy's `tryAssign` copy-hint preference (`CopyHintColours`)
  already does the lightweight coalescing that matters (a copy whose hint reg is
  free collapses to identity and `CopyEliminationPass` drops it). The standalone
  aggressive coalescer only adds the *over-merges* greedy's (current) splitter can't
  undo. **So the path to one allocator does NOT route through the standalone
  coalescer.** llvm-mos does run a separate `RegisterCoalescer` before greedy, but
  it relies on SplitKit to undo over-merges; until we build that SplitKit, greedy's
  inline copy-hinting is the pragmatic, working substitute. Leave
  `RegisterCoalescerPass` gated/unused; do not wire it into greedy.

- **The viable path: greedy WITHOUT the standalone coalescer.** Measured
  (`IRIE_GREEDY` + `IRIE_NO_RELOC`, coalescer OFF): **no crash**, byte-identical to
  llvm on every straight-line chain (`add-i16`/`add-i32`/`sub-i16`), and only
  **+15 / +4.8 %** over the colourer across the 17 (early-return +2, fibonacci +4,
  if-else +3, loop-counter +2; the rest at parity). The residual is the GPR-vs-zp
  placement quality (fact 2), addressable — if we choose — by the **full
  `getRegAllocationHints` copy-cost score** (not the net-neutral binary C1). It is
  NOT a correctness blocker.

- **C2 / region split** — only if the +4.8 % residual is deemed worth more. A real
  SlotIndex SplitKit (which also unblocks the coalescer) or llvm's `tryBlockSplit`
  (no EdgeBundles) would be the vehicle. **Do not build speculatively** — the
  residual is small; likely accept it.

**Revised order of attack (2026-06-25, after the coalescer investigation):**
1. **Make greedy default WITHOUT the standalone coalescer** — it is correct,
   terminating, byte-identical on straight-line, +4.8 % cross-block. Flip
   `useGreedy`, delete the colourer + the 3 load funnels +
   `InsertRelocationCopiesForConstrainedDefs`, regenerate goldens, accept the small
   residual. This reaches **one allocator** without the SplitKit work.
2. *(optional, later)* full `getRegAllocationHints` copy-cost score to shave the
   cross-block residual.
3. *(optional, much later)* real SlotIndex SplitKit → then the standalone coalescer
   can be wired in for the last few percent. Big; only if justified.

### Then: converge to one allocator

Flip `useGreedy` default; wire `RegisterCoalescerPass` into the greedy path
(currently `IRIE_COALESCE`-gated); **delete the colourer + ALL scaffolding** — the
3 load funnels, `InsertRelocationCopiesForConstrainedDefs`, `ComputeAllowedColours`
/ `TrySplitToRegister`, and any now-dead greedy split-insertion kinds — in one
sweep. Regenerate goldens the harness way; confirm `irie-report` holds; pin the
three frame-slot nondeterminism cases to the harness form.

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

# Scope: build a real SlotIndex SplitKit (decided 2026-06-25)

> **Fork chosen.** The "Revised order of attack" above lands on *make greedy
> default WITHOUT the standalone coalescer* (correct, terminating, +4.8 %, no
> SplitKit). The maintainer has chosen the **other fork**: build the faithful
> **SlotIndex/segment SplitKit** instead — it is the *primary missing gap*, the
> thing that makes the standalone `RegisterCoalescer` safe to wire into greedy,
> and what gets us to **one allocator on the llvm-mos pipeline shape** rather than
> a working-but-divergent greedy-minus-coalescer. The bespoke
> greedy-without-coalescer carrier-preference path (C1 binary predicate, the full
> `getRegAllocationHints` score) is set aside; that quality lever can ride on top
> of SplitKit later if the residual still matters. This section is the
> self-contained scope; everything needed to execute is restated here.

## What "SplitKit" means in Irie — and what we DON'T build

LLVM's `SplitKit.cpp` is ~1959 lines + `LiveRangeEdit.cpp` ~426. The bulk of that
is machinery Irie's architecture makes **unnecessary**. Two facts collapse the
scope dramatically:

1. **Reanalysis, not incremental liveness.** Every allocator round already
   recomputes `LiveIntervals` from scratch (`RegisterAllocatorPass` spill loop;
   the colourer, greedy, and the coalescer all rely on this). So we **drop**
   LLVM's incremental-update core: `transferValues`, `extendPHIRange`,
   `hoistCopies`, the `ValueMap` (parent-VN → new-interval-VN), `RegAssignMap`,
   and `LiveRangeEdit`'s delta tracking. A split is just an **IR edit**; the next
   reanalysis reconstructs all liveness. (Same trade `SplitEditor.cs`,
   `GreedyRegisterAllocator.cs`, and `RegisterCoalescerPass.cs` already document.)
2. **PhiElimination runs BEFORE RA.** At RA time there are **no block parameters
   and no `BlockTarget.Args`** (CLAUDE.md pipeline step 4 before step 6). A value
   that crosses a block boundary is just a vreg used in a successor, live across
   the edge — there are **no PHIs**. So we **drop** the single hardest part of
   LLVM SplitKit: PHI value reconstruction / SSA repair across split boundaries.

| LLVM SplitKit piece | Irie |
|---|---|
| `SplitEditor` open/enter/leave/use/finish + `RegAssignMap` + `ValueMap` | **replace** with one `SplitAtPoint(value, slot)` IR-edit primitive (below) |
| `transferValues` / `extendPHIRange` / `hoistCopies` (incremental liveness) | **drop** — reanalysis rebuilds |
| `LiveRangeEdit` (delta-tracked new intervals) | **drop** — reanalysis rebuilds |
| PHI reconstruction across boundaries | **drop** — no PHIs post-PhiElim |
| `SplitAnalysis` (per-block use summary) | **keep, minimal** — per-block first/last use of the value |
| `tryLocalSplit` (gap split in one block) | **keep** — already exists in `SplitEditor.cs`, re-express on the primitive |
| `tryBlockSplit` (split around each block with uses, no EdgeBundles) | **build** — the simple cross-block lever |
| `tryRegionSplit` (EdgeBundles + `SpillPlacement` + `InterferenceCache`) | **do NOT build** — defer indefinitely; the residual is small |
| edge splitting for boundary copies | **reuse** `PhiEliminationPass.SplitMultiSuccessorEdgesIntoParamBlocks` + `BranchLowering` |

Net new engineering: a **value-numbering addition to liveness**, **one split
primitive**, **`SplitAnalysis`-lite**, **`tryBlockSplit`**, and the **rewiring**
of the greedy ladder + coalescer onto them. No EdgeBundles, no incremental
liveness, no PHI repair.

## The core gap this fixes: CFG-correct interior renaming

The current `SplitEditor.RelocateAcrossClobber` (and
`InsertRelocationCopiesForConstrainedDefs`) decide which uses to redirect by a
**program-order slot window** `(producingDefSlot, nextDefSlot)`. That is only
correct for **straight-line code** — which is exactly why `add-i16`/`add-i32` are
byte-identical and why the cross-block cases regress. With branches/loops,
"slot N lies after the def and before the next def" is **not** the same as "the
value flowing into this use is the one that crossed the split point": a use in a
sibling/back-edge block can fall in the slot window yet read a *different* reaching
definition. SplitKit's real job is to answer that **per value number via the CFG**,
not by slot order.

This is also the exact mechanism of the **coalescer crash** (falsified-hypothesis
section above): the aggressive coalescer merges arg-byte + tied accumulator +
result-that-outlives into one **multi-def** vreg (`%3 = copy $a` / `%3 = adc
%3(tied),…` / `$a = copy %3`). A use-redirect keyed on the whole vreg's slot
window cannot isolate *one* of those value numbers to relocate — so the
across-clobber span never shrinks and the termination assertion trips. **Per-value-
number splitting is precisely what makes a coalesced mega-vreg tractable**, and
therefore what makes the standalone coalescer safe to enable.

## Data-model change: value numbers on `LiveIntervals`

`LiveInterval` today is just a list of `LiveSegment(Start, End)` — **no VNInfo**
([`Analyses/LiveIntervals.cs`](src/Irie/Passes/Analyses/LiveIntervals.cs)). Add a
value number to each segment: the identity of the def that produced it (use the
def's **def-slot** as the value-number id — unique per def, already computed).

- `LiveSegment(int Start, int End, int ValNo)` — `ValNo` = the def-slot of the
  producing def (or the block-entry slot for a live-in with no in-function def).
- `LiveIntervalsAnalysis.BuildVRegSegmentsForBlock` already tracks `openAt` per
  def point; tag each emitted segment with the def-slot that opened it. A live-in
  segment inherits the value number flowing in (carry it across the live-in/live-
  out sets — a small extension to the existing dataflow, or recompute by walking
  preds; the cross-block carry is the only new bit).
- `Normalize` must **only merge touching segments with the SAME `ValNo`** (today
  it merges purely on adjacency). Two segments of different value numbers that
  abut stay distinct — that is what lets a split target one value number.

Computed fresh every reanalysis (consistent with the reanalysis philosophy —
no VNInfo is persisted across rounds). This is the load-bearing change; budget
the most care here and unit-test it directly (straight-line, in-block re-def with
hole, cross-block live-out, two-address tied through-segment).

## The split primitive: `SplitAtPoint(value, splitSlot)`

One operation replaces the four bespoke relocation kinds
(`TrySplitConstrainedDefResult`, `TrySplitIncumbentAcrossClobber`,
`TrySplitRelocatableAcrossPhysClobber`, and the cross-block half of
`RelocateAcrossClobber`):

1. Identify the **value number** of `value` live across `splitSlot`.
2. Mint a fresh vreg `V'` (flexible class for a relocation; same class for a
   plain cut).
3. Insert `V' = pseudo.copy value` at `splitSlot` (before/after the instruction,
   or — if the split point is a CFG edge — in a split block; see edge splitting).
4. **Rename downstream uses via the CFG**: every use of `value` that is reached
   from `splitSlot` without passing through another def of `value` — i.e. uses
   belonging to the same value number, on the dominated/reachable side of the
   split — is rewritten to `V'`. This is a forward CFG reachability walk from the
   split point, stopping at re-defs, **not** a slot-order window. (Post-PhiElim,
   no block-arg fixup is needed; cross-block uses are plain operands.)
5. Record `V'` in `splitProducts` (never re-split) and return — the pass
   reanalyses.

Correctness rests on the reachability walk + value-number identity. A small
**dominator / reachable-from-point** helper is needed (none exists today; grep
confirmed only `HoistCommonCodePass` has ad-hoc CFG logic). For the function sizes
this target sees, an on-demand forward reachability set from the split block
(intersected with the value's live segments of that value number) is adequate —
no persistent dominator tree required.

## Policy layer: rebuild the `trySplit` ladder on the primitive

`GreedyRegisterAllocator.TrySplit` currently fans out to five hand-specialised
kinds. Re-express as the llvm `trySplit` ladder, cheapest first, all routed
through `SplitAtPoint`:

1. **`tryLocalSplit`** — keep the existing gap-based in-block logic
   (`SplitEditor.TryLocalSplit`), but cut via `SplitAtPoint` so the renamed side
   is value-number-correct.
2. **`tryInstructionSplit`** — keep (the copy-defined narrowing-use split).
3. **`tryBlockSplit`** — **new.** For a value that fits no single register over
   its whole cross-block range, split around **every block that uses it**
   (`splitSingleBlock` analogue): enter the value into a fresh interval at each
   using block's first use, leave it after the last, remainder spills. No
   EdgeBundles, no cost model — the simple fallback LLVM falls back to when region
   split doesn't pay. This is the cross-block lever that the slot-window relocation
   approximated incorrectly.
4. **`trySplitAroundCall`** — keep (re-express on the primitive); already faithful.

The constrained-def-result / incumbent / relocatable-phys-clobber kinds **fold
into `tryBlockSplit` + `tryLocalSplit`**: a value pinned to `{R}` and live across
a re-clobber of R is just a value that cannot hold one register across a region —
the generic splitter cuts it at the clobber. Delete the three bespoke kinds once
the generic path covers them (validate against the same goldens).

## Edge splitting (reuse existing infra)

When a split boundary is a CFG **edge** (the value is live-out of A into B and the
copy must sit on the A→B edge, not in A or B), reuse the critical-edge split
already in [`PhiEliminationPass.SplitMultiSuccessorEdgesIntoParamBlocks`](src/Irie/Passes/PhiEliminationPass.cs):
create a split block, redirect A's terminator to it, emit the boundary copy there,
and end it with `BranchLowering`'s unconditional jump (it runs post-isel, so the
branch is a real `mos6502.jmp.abs`, not a generic `cf.br`). Factor that edge-split
into a shared helper both passes call.

## Termination

The new ladder terminates by the **stage ladder + split-products-never-re-split**
discipline already in place (`LiveRangeStage New→Split→Spill`; `splitProducts`),
exactly as LLVM bounds work. Once splits are value-number-precise, the bespoke
**well-founded `ConstrainedRangeMeasure` assertion** (`RegisterAllocatorPass.cs`
~L159–168, `GreedyRegisterAllocator.ComputeConstrainedRangeMeasure`) becomes
redundant scaffolding — a value-number split always strictly shrinks the split
value's range on the relocated side. Keep the assertion as a tripwire through
bring-up, then delete it with the other scaffolding.

## The payoff: wire in the coalescer → one allocator

With value-number-precise splitting, the coalescer's multi-def mega-vregs are
tractable: greedy can cut any one value number out of a coalesced range. So:

1. Land SplitKit (stages below), greedy still flag-gated, colourer still default
   (oracle).
2. Wire `RegisterCoalescerPass` into the **greedy** path (currently
   `IRIE_COALESCE`-gated). Confirm the `add-i16` mega-vreg case that crashed now
   splits cleanly.
3. Flip `useGreedy` default; **delete the colourer + ALL scaffolding together** —
   the 3 load funnels, `InsertRelocationCopiesForConstrainedDefs`,
   `ComputeAllowedColours`/`TrySplitToRegister`, the `ConstrainedRangeMeasure`
   assertion, and the bespoke relocation split kinds. Removing the load funnels is
   then free (greedy splits `Ac`-class load results like any other).
4. Regenerate goldens the harness way; pin the three frame-slot nondeterminism
   cases (`AddressTakenFrameSlot`, `StaticFrameAllocChain`,
   `FrameSlotStructRoundtrip`) to the harness form; confirm `irie-report` holds /
   improves (region/block split should close part of the +4.8 % cross-block
   residual, e.g. the `early-return` Y-vs-zp carrier).

## Staging (each independently committable + green; colourer stays default until S5)

- **S1 — value numbers on liveness.** Add `ValNo` to `LiveSegment`; tag in
  `LiveIntervalsAnalysis`; value-number-aware `Normalize`; cross-block value-number
  carry. Unit tests only; no allocator behaviour change yet (nobody reads `ValNo`).
  Both suites green. Commit.
- **S2 — `SplitAtPoint` primitive + CFG reachability helper.** Build the primitive
  and the reachable-from-point use-rename. Unit-test the two-block / loop shapes
  the slot-window approach got wrong. Not yet wired into the ladder. Commit.
- **S3 — re-express existing greedy split kinds on `SplitAtPoint`.** Swap
  `tryLocalSplit` / `tryInstructionSplit` / `trySplitAroundCall` to the primitive;
  fold the three bespoke relocation kinds into the generic path. Greedy
  (flag-gated) must stay byte-identical on `add-i16`/`add-i32`/`sub-i16` and not
  regress the 17 reference cases under `IRIE_GREEDY`+`IRIE_NO_RELOC`. Commit.
- **S4 — `tryBlockSplit`.** Add the cross-block lever; measure the cross-block
  residual (`early-return`, `loop-counter`, `fibonacci`) drops toward the colourer.
  Commit.
- **S5 — wire coalescer into greedy; flip default; delete colourer + scaffolding.**
  The one-allocator convergence (payoff section). Regenerate goldens; confirm
  `irie-report`. Commit.

## Validation (every stage)

```
dotnet test --project src/Irie.Tests                 # unit + lit (incl. new VN + SplitAtPoint unit tests)
dotnet test --project src/DotNetro.Compiler.Tests    # emulator behaviour, not just shape
dotnet run -c Release --project src/Irie.Tools.Reference -- generate-report   # irie-report headline
```

## Risks / open questions

- **Value-number cross-block carry is the subtle bit.** The existing dataflow
  tracks vreg *liveness* but not *which value number* is live-in. Getting the carry
  wrong silently mis-renames a split. Mitigate: direct unit tests on the
  hole/re-def/loop shapes; cross-check a few against the colourer's (correct)
  output as oracle before flipping the default.
- **Reachability vs. true dominance.** A forward reachable-from-point set is a
  superset of the dominated set; for the rename we want "reached from the split
  without passing another def of the value." Confirm the stop-at-redef walk gives
  the dominance-equivalent answer for these CFGs (no irreducible loops on this
  target). If a counterexample appears, add a real dominator-tree helper (still
  cheap at these sizes) rather than widening the heuristic.
- **Don't drift into region split.** EdgeBundles/`SpillPlacement`/`InterferenceCache`
  are explicitly out of scope; `tryBlockSplit` is the ceiling. If S4's residual is
  still material, that is a *separate, later* decision — not scope creep here.
- **Golden-regeneration trap.** Regenerate/verify under `dotnet test`; pin the
  three frame-slot nondeterminism cases to the harness form.
