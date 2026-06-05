# Register Allocator Redesign Plan

Goal: rework Irie's `RegisterAllocatorPass` so its output approaches the quality
of the llvm-mos references in
[`design/register-allocator/reference-register-allocator/`](../design/register-allocator/reference-register-allocator).
"Quality" here means, in priority order: (1) it allocates *real* functions at
all (loops, diamonds, calls) instead of aborting; (2) it emits few redundant
copies; (3) it uses the cheap architectural registers ($a/$x/$y) where they
help, keeping $a free for arithmetic; (4) it spills rarely and cheaply, as the
abundant zero-page register file allows.

This plan is self-contained: it records what the current code does and why, so
execution does not require re-deriving it. File:line citations are to the state
at the time of writing.

---

## Guiding principles

These hold for the whole effort; they are not defaults to be traded away under
time pressure.

- **Solid foundation over quick wins.** Build the real thing and expand it
  thoughtfully. If a shortcut or hack starts to look tempting — "just special-case
  this one shape", "widen this class to make the error go away" — **stop and ask
  first** rather than taking it. The current pass is what accreted hacks look
  like; we are replacing it, not adding to it.
- **One liveness implementation.** The new physreg-aware analysis *replaces* the
  coarse vreg-only `LivenessAnalysis` outright. We verified nothing else consumes
  it (only `RegisterAllocatorPass` and its unit test —
  `IlToMirTranslator`'s `ComputeLocalLiveness` is a separate, unrelated IL-level
  computation), so the old analysis and its tests are deleted, not kept in
  parallel. Two overlapping analyses is exactly the debt to avoid.
- **Comment for a reader who has never written a register allocator.** Allocator
  code is notoriously opaque. Favour far more comments than feels necessary: every
  non-obvious step should say *what* it does, *why*, name the standard technique it
  implements, and cite the reference. Appel, *Modern Compiler Implementation*
  (ch. 11), is the canonical source for the algorithm below and the one to follow;
  a human should be able to understand the allocator from the comments alone.
- **Defer what you can model later.** Some problems (e.g. the register a copy needs
  as scratch) are better solved *after* allocation with exact information than
  worked around *inside* it. See §3.6.
- **Match decisions, not the algorithm.** The yardstick is output quality vs the
  llvm-mos corpus (§6), not bug-for-bug fidelity to LLVM's Greedy allocator.

---

## Reader's primer: how graph-colouring register allocation works

Skip if this is familiar. It defines the terms used throughout §3 and §7, for a
reader new to register allocation.

The job: every value the program computes lives in a virtual register (vreg); the
machine has only a few physical registers (physregs). We assign each vreg a
physreg, subject to one rule: **two values that are ever live at the same moment
cannot share a physreg.**

- **Live range / interval.** The span over which a value is live (from where it is
  produced to its last use). Two values *interfere* if their live ranges overlap.
- **Interference graph.** One node per value; an edge between two nodes that
  interfere. Assigning physregs = *colouring* this graph with K colours (K = number
  of registers) so no edge joins two same-coloured nodes. Key fact: a node with
  **fewer than K neighbours** can always be coloured, whatever its neighbours get.
- **Simplify / select (Chaitin–Briggs).** Repeatedly remove low-degree (< K) nodes
  and push them on a stack ("simplify"); removing a node lowers its neighbours'
  degree, so this cascades. Then pop nodes back one at a time and give each a colour
  none of its already-coloured neighbours used ("select").
- **Spilling.** If every remaining node has ≥ K neighbours, pick one to *spill*:
  keep it in memory, with a store after its definition and a load before each use.
  That shatters its long live range into tiny ones, lowering pressure; then re-run.
  **Briggs' optimistic colouring**: instead of spilling on the spot, push the
  high-degree node anyway and *hope* a colour is still free when we pop it — usually
  one is. Only if select truly finds no free colour do we actually spill.
- **Spill cost & rematerialization.** When forced to spill, spill the *cheapest*
  value — few uses, not in a loop. Better still, if a value is trivial to recompute
  (a constant, a symbol address), **rematerialize** it at each use rather than
  store/load it.
- **Coalescing.** A copy `a = b` costs nothing if `a` and `b` get the same register.
  *Coalescing* merges their nodes so the allocator must give them one register, and
  the copy then vanishes. But merging raises the merged node's degree and can make
  the graph un-colourable (a spill that costs more than the copy saved). So we
  coalesce only when a **conservative test** guarantees safety:
  - **Briggs' test:** the merged node has < K neighbours *of significant degree*
    (≥ K).
  - **George's test** (used when one side is a physreg): every neighbour of one node
    already interferes with the other, or is itself low-degree.
- **Freeze.** If a copy can't be coalesced safely yet but its nodes block
  simplification, *freeze* it — give up on removing that copy, treat its nodes as
  ordinary, and keep simplifying.
- **Precoloured nodes.** Physregs forced by the program — call-convention
  argument/return registers, an operand pinned to `$a`, the registers a call
  clobbers — are nodes already assigned a colour. Coalescing a vreg with a
  precoloured node is how a value is steered onto the exact register it must occupy,
  removing the copy.
- **Iterated register coalescing (George & Appel).** All of the above as one loop:
  simplify → coalesce → freeze → mark-for-spill, repeating until the graph is empty,
  then select. This is the algorithm we implement; Appel ch. 11 gives complete
  pseudocode and is worth reading before starting.

LLVM's "Greedy" allocator (which built the corpus references) reaches similar
decisions by a different route — priority-ordered assignment with eviction and
live-range *splitting*. We choose iterated coalescing because our hardest problem
is copy removal, which it targets directly, and because splitting — Greedy's main
extra power — rarely pays on a target with 30 zero-page registers.

---

## 1. Where we are today

### 1.1 The pipeline around RA

`iriec` runs ([src/Irie.Tools.Compiler/Program.cs:63-72](../src/Irie.Tools.Compiler/Program.cs#L63-L72)):

```
FrameLowering → AbiLowering → Legalizer → InstructionSelector
  → PhiElimination → TwoAddressInstruction → RegisterAllocator
  → CopyElimination → <target post-RA passes> → PseudoExpansion
```

So by the time RA runs, SSA is already destroyed (PhiElimination replaced block
params with copies; TwoAddress broke single-def by re-defining tied vregs).
Every vreg carries a `ClassedVReg` annotation; the IR is a mix of generic
`pseudo.copy` plumbing and selected target ops with physreg operands.

### 1.2 What RA does now ([RegisterAllocatorPass.cs](../src/Irie/Passes/RegisterAllocatorPass.cs), 671 lines)

A single linear-scan pass wrapped in a lot of compensating machinery:

1. **Three "widen to flexible class" pre-passes** (`WidenLiveinsToFlexibleClass`,
   `WidenUnconstrainedToFlexibleClass`, `WidenLeftoverTypedToFlexibleClass`,
   lines 70-180). These undo instruction-selection's habit of pinning vregs to a
   single-physreg class (`Ac`) when the value is actually free to live anywhere
   8-bit. The middle one runs a "touched only by `pseudo.copy`" analysis to
   decide. Brittle, heuristic, and entirely about papering over class
   over-constraint upstream.
2. **`InsertConstraintFixupCopies`** (200-248): where an operand's required class
   ≠ the vreg's class, insert a `pseudo.copy` into a fresh vreg of the required
   class.
3. **`InsertResultPreservationCopies`** (262-322): after a tied-def whose value
   is read later, insert a `pseudo.copy` so the tied physreg can be reused by the
   next chain link. Block-local only — multi-block has a standing TODO (line 314).
4. **Liveness**: vreg-only, single `[Start,End]` interval per vreg, *no holes*
   ([LivenessAnalysis.cs](../src/Irie/Passes/Analyses/LivenessAnalysis.cs);
   [Liveness.cs:13](../src/Irie/Passes/Analyses/Liveness.cs#L13) explicitly notes
   "single conservative range (no hole tracking)"). Cross-block ranges are
   widened to whole blocks.
5. **Linear scan** (`LinearScanAllocate`, 345-409): order by start, a single
   `physregActive` dict, expiry rule `end ≤ start`, one copy-hint form
   (`%v = pseudo.copy $P` hints `%v→$P`). The other hint directions are
   deliberately suppressed (576-584) because, *without live-range splitting*,
   they drag scarce single-physreg registers into long-lived vregs.
6. **Physreg liveness reconstructed ad-hoc**, because the liveness analysis
   ignores physregs:
   - `ComputeClobberSlots` (420-456): implicit-def physreg operands + a
     target hook (`GetPseudoCopyScratchClobbers`) for $a-scratch in copy lowering.
   - `ComputePhysRegReservations` (475-525): tracks call-result physregs that are
     read back via `pseudo.copy %save = $P`, reserving `[defSlot, readSlot]`.
   Both exist only to recover information a physreg-aware liveness would just have.
7. **No spilling** (throws, 398-402), no eviction, no live-range splitting, no
   real coalescing. `CopyEliminationPass` afterwards only deletes *identity*
   copies (`$a = pseudo.copy $a`).

### 1.3 Register model ([MOS6502RegisterInfo.cs](../src/Irie/Target/MOS6502/MOS6502RegisterInfo.cs), [MOS6502RegisterClass.cs](../src/Irie/Target/MOS6502/MOS6502RegisterClass.cs))

- Classes: `Ac`($a), `Xc`($x), `Yc`($y), flag classes (`Cc`/`Nc`/`Vc`/`Zc`/…),
  `Imag8` (zero-page RC2..RC31, 30 regs), `Anyi8` = flexible 8-bit.
- `Anyi8` allocatable order = `[RC2..RC31, $a, $x]` — **RC-first, and $y is not
  in it at all**. This is why Irie parks data in zero page and never uses $y,
  whereas llvm-mos uses $y freely (see the AddI16 divergence below).
- `FlexibleI8ClassId = Anyi8`.
- No 16-bit pointer-pair class (llvm-mos has `$rs` pairs); Irie has none, which
  is consistent with `mem.*` only supporting `mem.symbol` addresses today.

### 1.4 What the corpus conversion already taught us

From `src/Irie.Tests/Lit/CodeGen/MOS6502/RaCorpus/` and its README:

- **The single biggest gap is that RA cannot spill.** Even a plain `if/else`
  diamond or a counted loop aborts with "no free physical register". Only ~12 of
  46 reference cases were convertible, almost all blocked at RA by pressure.
- **Copy bloat / register-preference divergence.** For `add-i16`, llvm-mos parks
  the low result byte in real register `$y`; Irie parks it in `$zp2` and never
  uses `$y`. Irie's output is also `pseudo.copy`-heavy.
- **Adjacent bug** (in `PhiEliminationPass`, not RA, but it blocks branchy RA
  tests): a `cf.cond_br` carrying block-args on its edges leaves stray vregs
  after RA — the pass inserts phi-copies before the *shared* terminator and its
  critical-edge guard ([PhiEliminationPass.cs:33-35](../src/Irie/Passes/PhiEliminationPass.cs#L33-L35))
  misses the two-edges-with-args case.

### 1.5 Diagnosis

The current pass works for straight-line, low-pressure arithmetic and nothing
else. Its structure fights itself: **liveness models only vregs**, so a stack of
ad-hoc reconstructions (clobber slots, reservations, scratch hooks) re-derive
physreg interference; **isel over-constrains classes**, so a stack of widen
heuristics undo it; **copies are inserted eagerly and never coalesced**, so the
output is copy-heavy and a `CopyElimination` afterthought only removes the
trivial ones. None of spilling, eviction, splitting, or coalescing — the things
that make llvm-mos good — exist.

The right move is to keep the parts that are genuinely sound and rebuild the
core around two foundations the current design lacks: **physreg-aware live
intervals with holes**, and **a real coalescer**.

---

## 2. What makes the llvm-mos (LLVM Greedy) output good, and what matters here

LLVM's allocator = Greedy (assign/evict/split/spill on precise live intervals) +
Virtual Register Rewriter. The quality levers, ranked by how much they matter on
*this* target (abundant zero-page register file, scarce $a, arithmetic pinned to
$a):

1. **Precise live intervals including physregs.** Interference is exact (holes
   tracked); calling-convention clobbers and fixed-register operands are just
   fixed intervals. Everything else builds on this. — *Essential foundation.*
2. **Copy coalescing / hint-biased coloring.** Copy-related vregs (phi copies,
   tied operands, CC moves, call-result relocations) get hinted to the same
   physreg; surviving copies become identities and vanish. — *The biggest
   quality delta for Irie; this is where the copy bloat goes away.*
3. **Spilling** with spill-cost heuristics and **rematerialization** of cheap
   defs (constants). — *Required for coverage; on this target the zp file makes
   it rare, and remat of `arith.constant` is cheap and high-value.*
4. **Register preference / cost.** Use $a/$x/$y when beneficial, keep $a free for
   arithmetic, prefer the abundant zp pool for long-lived values. — *Tuning that
   closes the "$y" gap.*
5. **Live-range splitting** (region/per-block/around interference). — *Greedy's
   signature, but lowest priority here: the zp file means we rarely hit the
   pressure where splitting beats spilling. Defer until a case demands it.*

We do not need to clone Greedy to match its *decisions* on these small
functions. We need exact interference, strong coalescing, competent spilling,
and a sensible preference order.

---

## 3. Target architecture

**Recommended core: graph-coloring allocation with iterated register coalescing
(Briggs / George–Appel), over a physreg-aware live-interval + interference
foundation.** Rationale for choosing this over a Greedy port:

- Irie's defining weakness is copy bloat; iterated register coalescing is the
  textbook answer and is *conservative* (never introduces spills by coalescing).
- It unifies all the copy sources we currently special-case — phi copies, tied
  operands, constraint-fixup copies, call-result relocations — into one
  mechanism: coalesce when safe, leave a real move when not.
- Spilling (optimistic coloring + spill cost) is well specified.
- Splitting — Greedy's main advantage — is the least valuable lever here, so the
  simpler core loses little.

Alternatives explicitly considered and deferred: (a) a faithful Greedy port
(priority queue + eviction + splitting) — more code, splitting rarely pays here;
(b) SSA-based RA (color on the chordal SSA interference graph, coalesce after) —
attractive, but our pipeline destroys SSA *before* RA (PhiElim + TwoAddress), so
adopting it means re-ordering the pipeline; keep that in our back pocket but do
not block on it. We stay post-SSA and let the coalescer clean up.

### 3.1 The foundation: `LiveIntervals` (physreg-aware, with holes)

A new analysis, `LiveIntervals`, that **replaces** the coarse vreg-only
`LivenessAnalysis` outright. RA is its only consumer (verified), so the old
analysis and its unit tests are deleted, not kept alongside it (see Guiding
principles):

- One global instruction numbering (keep the existing slot scheme, but number at
  finer granularity — distinguish a def point from a use point so a value and its
  consumer can share a register precisely; LLVM uses slot indices with
  register/use sub-slots).
- For **each vreg**: a list of live *segments* (holes between them), not one
  `[start,end]`.
- For **each physical register**: live segments derived from
  - entry-block CC live-ins,
  - explicit physreg operands (copies to/from $a/$x/flags, CC arg/return setup),
  - implicit-defs/uses declared in `DialectInstructionInfo` (call clobbers, flag
    definitions — e.g. `mos6502.cmp` defines `$n/$v/$z`, `mos6502.jsr.abs`
    clobbers caller-saved regs).
- Interference: two intervals interfere iff any of their segments overlap.
  Precolored physreg intervals interfere with overlapping vreg intervals.

This single analysis **subsumes and retires** `ComputeClobberSlots`,
`ComputePhysRegReservations`, and the bespoke half-open overlap tests — they all
become "does this vreg's interval overlap this physreg's busy interval".
Crucially, copy *scratch* clobbers are **not** represented here at all any more:
they stop being a register-allocation concern and move post-RA (§3.6).

### 3.2 Class constraints without the widen hacks

A vreg's allocatable set = intersection of the register classes implied by all
its defs and uses. The current widen passes exist because isel pins values to
`Ac` that are not truly $a-only. Two ways to fix, in preference order:

- **Preferred (move the concern to isel):** isel assigns the *natural broad*
  class (`Anyi8`) to flexible values and inserts an explicit `pseudo.copy` into
  `Ac` only at the operand that truly requires $a. The coalescer then removes
  that copy whenever $a is free. This is exactly how `TwoAddressInstructionPass`
  already handles tied operands, and how llvm-mos models fixed-reg operands.
- **Interim (keep isel as-is):** compute the class-intersection in RA and treat a
  single-physreg class as a *hint/constraint only when forced*. The coalescer
  subsumes most of what the widen passes did. Delete the three widen methods once
  the coalescer + intersection cover their cases (guarded by the existing lit
  tests, whose expected output will change deliberately).

### 3.3 The coalescer

Iterated register coalescing over the interference graph:
- Build move-related edges from every `pseudo.copy` (and tied-operand pairs).
- Conservative coalescing tests (Briggs: combined node has < K neighbors of
  significant degree; or George, for precolored). Precolored nodes = physregs;
  coalescing a vreg with a physreg = assigning it that register if safe.
- Freeze + spill as usual.
- A coalesced copy disappears entirely (not merely turned into an identity for a
  later pass to clean — though keeping `CopyEliminationPass` as a safety net is
  fine).

This is where the AddI16 copy bloat and the call-result relocation copies
collapse.

### 3.4 Spilling

- Spill-cost per interval (uses/defs weighted by loop depth; ∞ for unspillable
  fixed intervals).
- **Rematerialization first** for trivially re-computable defs (`arith.constant`,
  `mem.symbol`) — recompute at the use instead of reload. High value, cheap.
- Otherwise spill: store after each def, reload before each use, rewrite to fresh
  tiny intervals, re-run coloring (Briggs optimistic loop).
- **RA mints *abstract* spill slots, it does not place them.** A spill slot is a
  frame index (a `mem.frame_addr` store/load), nothing more. *Where* that slot
  physically lives — zero-page frame budget (≈ RC20..RC29) first, `.bss` beyond —
  is decided later by static stack allocation, not by RA. This decoupling is what
  lets us build RA before the static-stack work (see §8, which answers the
  sequencing question directly). The register scavenger (§3.6) shares the same
  abstract-slot mechanism for its emergency save/restore. The one contract to fix
  up front is the *shape* of an abstract spill slot, so Phase 4 and the future
  static-stack pass agree on it.

### 3.5 Register preference (closing the "$y" gap)

- Add `$y` to the flexible allocation order, and make order *hint- and
  cost-driven* rather than a fixed `RC-first` list:
  - Copy/CC hints dominate (biased coloring).
  - Absent a hint, bias *short* live ranges and values feeding $x/$y-capable ops
    toward $x/$y; bias *long* ranges that span many arithmetic ops toward the zp
    pool, keeping $a free for the ADC/etc. chain.
- This is a tuning phase; its acceptance test is convergence toward the llvm-mos
  references on the corpus (§6), not a fixed rule.

### 3.6 Copy scratch: solved *after* allocation, not modelled during it

Today RA pre-commits to "a `pseudo.copy` clobbers `$a`" via
`GetPseudoCopyScratchClobbers`
([MOS6502RegisterInfo.cs:80](../src/Irie/Target/MOS6502/MOS6502RegisterInfo.cs#L80))
and reserves `$a` around such copies. This over-constrains the allocator (it keeps
`$a` free even when `$x`/`$y` would have served) and feeds the clobber-slot
machinery we want to delete.

**llvm-mos does not model this during register allocation at all** — investigated
in its source for this plan, and the answer is worth following:

- Its allocator allocates as if `COPY` were free.
- `COPY` is lowered to real instructions *after* RA
  (`MOSInstrInfo::copyPhysReg`). A copy needing a scratch GPR — zero-page→zero-page,
  or `$x`↔`$y` on a plain 6502 — is emitted with a **fresh virtual register** for
  the scratch (`getRegWithVal` → `createVReg`), and it even peepholes a backward
  scan to reuse a GPR that already holds the value.
- A dedicated pass, `MOSPostRAScavenging`, then runs LLVM's **register scavenger**
  over those scratch vregs: with exact post-RA liveness it picks whatever GPR is
  *dead at that exact point* (often `$x` or `$y`, not `$a`), and only if none is
  free does it **save/restore** one through an emergency frame slot. Its own
  comment: "these pseudos (including COPY) often require temporary registers on
  MOS … they emit virtual registers instead, and this pass performs register
  scavenging to assign them … freeing them up via save and restore if necessary."
- Confirmed pass order in `MOSTargetMachine.cpp`: register allocation → zero-page
  alloc → post-RA scavenging → post-RA pseudo expansion → scavenging again → … →
  **static stack alloc last**.

**Adopt the same shape in Irie.** Drop `GetPseudoCopyScratchClobbers` and the
scratch-clobber concept from RA entirely. Instead:

1. In/after `PseudoExpansionPass`, lower a `pseudo.copy` that needs scratch into a
   form whose scratch operand is a **fresh vreg** (today `MOS6502PseudoExpander`
   hardcodes routing through `$a`).
2. Add a small post-RA `RegisterScavengingPass` that assigns each scratch vreg the
   cheapest physreg dead at that point — prefer a free GPR (`$a`/`$x`/`$y`), fall
   back to save/restore via an emergency abstract slot (§3.4).

This produces *better* code than today (it can use `$x`/`$y` as scratch instead of
forcing `$a` free) and removes a whole class of constraint from the allocator. It
is a real pass with its own tests — explicitly **not** a shortcut.

---

## 4. What to keep, change, and delete

**Keep:**
- `TwoAddressInstructionPass` — tied-operand → copy is the standard pre-RA shape;
  the coalescer is designed to consume exactly these copies.
- `PhiEliminationPass` — *after* fixing the cond_br/critical-edge bug (§5).
- `CopyEliminationPass` — demote to a safety net behind the coalescer.
- The register-class model (`MOS6502RegisterClass`/`RegisterInfo`), extended with
  `$y` in the flexible order and per-class preference metadata.
- The slot-numbering idea (refined to def/use sub-slots).

**Change / add:**
- Replace `LivenessAnalysis` with the physreg-aware `LiveIntervals` analysis with
  holes (§3.1), and **delete** the old analysis + its unit tests — nothing else
  consumes it (verified).
- New post-RA `RegisterScavengingPass` to fill copy-scratch vregs (§3.6); teach
  `PseudoExpansionPass` / `MOS6502PseudoExpander` to emit scratch as a fresh vreg
  rather than hardcoding `$a`.
- Instruction selection: emit broad classes + explicit constraint copies (§3.2,
  preferred path) so RA never has to "widen".

**Delete (folded into the new core):**
- `WidenLiveinsToFlexibleClass` / `WidenUnconstrainedToFlexibleClass` /
  `WidenLeftoverTypedToFlexibleClass`.
- `InsertConstraintFixupCopies` (replaced by isel constraint copies + coalescer).
- `InsertResultPreservationCopies` (falls out of interference + coalescing; the
  multi-block TODO disappears with it).
- `ComputeClobberSlots`, `ComputePhysRegReservations`, `IsClobberFree`,
  `IsReservationFree` (subsumed by physreg intervals).
- `GetPseudoCopyScratchClobbers` and the whole scratch-clobber concept (copy
  scratch is chosen post-RA by the scavenger; §3.6).
- The single-direction copy-hint hack (replaced by the coalescer's hint biasing).

---

## 5. Prerequisite fixes / dependencies

- **PhiElimination cond_br bug.** Split critical edges (and handle a conditional
  branch whose two edges both carry args) before phi copies are inserted, so no
  block-args survive into post-RA MIR. This unblocks all branchy RA tests and is
  worth doing first, independently. (Found during corpus conversion;
  [PhiEliminationPass.cs:31-61](../src/Irie/Passes/PhiEliminationPass.cs#L31-L61).)
- **Frame/spill model.** Spilling (Phase 4) needs only an *abstract* spill-slot
  abstraction — a frame index RA can mint — **not** finished static-stack
  placement. RA emits abstract slots; static stack allocation lowers them later.
  So this is not a blocker and the ordering is correct (RA before static-stack);
  §8 answers the sequencing question in full. Phases 0–3 need no frame model at
  all. The only thing to agree up front is the abstract-slot shape.
- **Finer instruction numbering** (def vs use sub-slots) if the current single
  slot per instruction proves too coarse for exact def/use sharing.

---

## 6. Validation strategy

The reference corpus is the yardstick. Acceptance is measured, not eyeballed:

- **Add a corpus-diff harness.** For each convertible RaCorpus case, compute
  (instruction count, `pseudo.copy`/move count, count of $a/$x/$y vs zp uses) for
  Irie's `--emit=asm` output and compare to the matching
  `reference-register-allocator/**/*.mir`. Report deltas; track them over time.
- **Convergence target.** Each phase should monotonically reduce the copy-count
  and instruction-count deltas vs the references, and should *grow* the set of
  convertible cases (spilling unlocks loops/diamonds/struct/pointer cases).
- **Regression gate.** Keep green (or deliberately update, reviewing the diff):
  - unit tests `src/Irie.Tests/Passes/RegisterAllocatorTests.cs`,
  - lit tests `IntegerAdd32-`, `IntegerSub32-`,
    `ConstantIntegerAdd32Pressure-RegisterAllocator.irie`,
  - the 12 `RaCorpus/*-RegisterAllocator.irie` characterization tests.
  These are *characterization* tests: when the allocator legitimately improves,
  regenerate the CHECK blocks (the generator approach used to author them) and
  review that each change is an improvement toward the reference.
- **As spilling lands, expand RaCorpus.** Convert the cases currently blocked by
  "RA can't spill" (if-else, loop-counter, fibonacci, …) and pin the new output.

---

## 7. Phased implementation

Each phase is independently shippable and keeps the suite green (updating
characterization output deliberately). PR per phase.

**Phase 0 — Prerequisite: fix PhiElimination cond_br/critical edges.**
Split critical edges; lower conditional-edge phi args correctly. Add lit tests
for cond_br-with-args on both edges. Unblocks branchy RA work. (Independent of
everything below.)

**Phase 1 — `LiveIntervals` foundation.**
New physreg-aware interval analysis with holes (§3.1) + interference queries.
Unit tests for: vreg holes, physreg clobber intervals from implicit-defs, CC
live-ins, copy scratch. No behavior change to RA output yet — just stand up the
analysis and prove it reproduces today's clobber/reservation conclusions on the
existing tests.

**Phase 2 — Re-core RA on `LiveIntervals`; retire the physreg-liveness hacks +
the copy-scratch hack.**
Replace `LinearScanAllocate` + `ComputeClobberSlots` +
`ComputePhysRegReservations` with allocation over the interference graph. Keep
*current* coalescing behavior (still none) so output is ~unchanged; the win is
deleting ~250 lines of ad-hoc reconstruction and getting exact interference.
In the same phase, land the post-RA `RegisterScavengingPass` + scratch-as-vreg
copy lowering and delete `GetPseudoCopyScratchClobbers` (§3.6) — these are
coupled: once RA no longer reserves `$a` for copies, the scavenger must be the
thing that picks copy scratch. Still no spilling (the scavenger's emergency
save/restore is the only memory traffic, and is rare).

**Phase 3 — Coalescer.**
Iterated register coalescing (§3.3). Fold tied-operand, constraint, phi, and
call-relocation copies into it. Delete the widen passes and
`InsertConstraintFixupCopies`/`InsertResultPreservationCopies` (move class
constraints to isel per §3.2, or compute intersections in RA). *Expected: large
drop in copy count; AddI16-style output converges toward the reference.* Update
characterization tests.

**Phase 4 — Spilling + rematerialization.**
Spill cost, optimistic coloring loop, remat of `arith.constant`/`mem.symbol`,
and spill/reload to **abstract** frame slots (§3.4) — concrete placement deferred
to static-stack (§8). *Unblocks the largest block of skipped corpus cases.*
Convert if-else / loop-counter / fibonacci / etc. into new RaCorpus tests. Revisit
`static-stack-alloc-plan.md` after this lands, now that allocation is reliable.

**Phase 5 — Register preference / cost tuning.**
Add `$y`; hint- and cost-driven ordering (§3.5). Tune against the corpus-diff
harness until $a/$x/$y usage tracks the references. *Closes the "$y" quality gap.*

**Phase 6 (optional) — Live-range splitting.**
Only if a real workload hits pressure where splitting beats spilling. Region /
per-block splitting à la Greedy. Likely unnecessary given the zp register file;
revisit when a concrete case demands it.

---

## 8. Risks & open questions

- **Doing RA before `static-stack-alloc` is the right order — not a problem.**
  (Raised directly.) Two reasons it holds. (1) It is exactly llvm-mos's order: its
  Greedy allocator runs first and `MOSStaticStackAlloc` is one of the *last* passes
  (confirmed in `MOSTargetMachine.cpp`). (2) RA does not need final frame
  addresses — when it spills, it mints an **abstract** spill slot (a frame index /
  `mem.frame_addr`), and a later pass (static stack allocation) decides where that
  slot physically lives (zero page vs `.bss`). The abstraction decouples them. And
  the converse is the user's own point: a *reliable* static stack needs to know
  what is live across calls and what got spilled — which is precisely what a
  finished allocator produces. So: build RA first, have it emit abstract slots, and
  revisit `static-stack-alloc-plan.md` afterwards to lower them. The only contract
  to fix up front is the *shape* of an abstract spill slot, so Phase 4 and the
  future static-stack pass agree.
- **Class-constraint relocation to isel (§3.2).** Moving constraint copies into
  the selector touches `MOS6502InstructionSelector` and could ripple into the
  legalizer's class assignments. If that proves invasive, take the interim path
  (intersection in RA) and defer.
- **Instruction numbering granularity.** Exact def/use register sharing (e.g. a
  value and its single consumer on $a) may need def/use sub-slots; the current
  single-slot-per-instruction scheme may force conservative interference. Decide
  in Phase 1.
- **Keeping post-SSA vs going SSA-RA.** Staying post-SSA leans hard on the
  coalescer to undo PhiElim/TwoAddress copies. If coalescing quality disappoints,
  reconsider SSA-based RA (color before phi elimination) — a larger pipeline
  change, hence not the default here.
- **Determinism.** Graph coloring with eviction/spill must be deterministic
  (stable orderings) so the characterization tests stay stable.
- **Y is also the index register.** As `$y` enters the data allocation pool, it
  competes with indexed addressing (`mem.*` indexed forms, once they exist).
  Model `$y` pressure from indexing as fixed intervals so data use of `$y`
  doesn't collide with an index need.
