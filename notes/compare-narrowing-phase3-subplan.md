# Sub-plan: Phase 3 of compare-narrowing (wide non-zero compares + `arith.select`)

Status: **Option A chosen (2026-06-17) — not yet started.** A motivating
`arith.select` consumer (see Option A, step 1) is a required part of the work, so
the materialized-select path has a real driver rather than being built
speculatively for the fused wide-compare case (which re-fuses it away). Options B
and A′ below are recorded as considered-and-not-taken.
Parent plan: [`compare-narrowing-in-legalizer.md`](compare-narrowing-in-legalizer.md) §"Phase 3".
Prereqs done: Phase 1 (signed-vs-zero narrowing in the legalizer) and Phase 2
(predicate normalization to `{eq, uge, slt}`) are landed and green.

## What Phase 3 was meant to do

Per the parent plan, Phase 3 moves the **wide (i16/i32) compare width-narrowing**
out of the instruction selector (`MOS6502InstructionSelector.SelectCmpIMultiByte`)
and into the legalizer, so the legalizer owns *all* compare width+predicate
lowering and isel keeps only the i8 compare + `cf.cond_br` fusion. It requires
new generic dialect ops (`arith.select`, `arith.xor`, and probably
`arith.and`/`arith.or`) and is explicitly gated in the parent plan on a "real
need… not done speculatively", with `arith.select` lowering flagged as possibly
warranting its own sub-plan. This document is that scoping.

## The reference (llvm-mos) and why Irie cannot copy it directly

`MOSLegalizerInfo::legalizeICmp`
(`/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSLegalizerInfo.cpp:1218`)
lowers wide compares as data-flow over an SSA flag op:

- **`== 0` feeding only branches** → `G_CMPZ` (multibyte OR-to-zero), selected to
  `CmpBrZeroMultiByte`.
- **wide unsigned / equality** → lexicographic
  `buildSelect(EqHigh, CmpRest, CmpHigh)` (cpp:1282-1305): recurse on the high
  byte and the rest, combine with a `G_SELECT`.
- **wide signed** → a multibyte `G_SBC` chain, then `N⊕V` computed by
  `EOR #$80`-iff-`V` + a re-examining `G_SBC` (cpp:1363-1391).

The 8-bit leaf is always a **`G_SBC`** that produces `result, C, N, V, Z` as
generic SSA values (cpp:1345-1397). When the resulting `i1` feeds a branch,
`MOSInstructionSelector::selectBrCondImm` (cpp:967) **re-fuses** the
`G_SBC`/`G_SELECT`/`G_CMPZ` chain back into a single `CmpBr*`/`CmpBrZeroMultiByte`
instruction via the `m_CmpNZ*` matchers — i.e. the data-flow boolean is collapsed
back into a branch tree at isel time, so the final code is a CMP/branch ladder,
not a materialized boolean.

**Two properties of Irie shape the design** (the first is the parent plan's
`G_SBC` Non-goal — treated here as a re-evaluable tradeoff, not a hard rule; see
Option A′):

1. Irie currently jumps straight from `arith.cmpi i8` to `mos6502.cmp` (physical
   flags) rather than producing a generic `G_SBC` that yields N/V/Z/C as SSA
   values. Keeping that means no `N⊕V`-via-V trick (no V as an SSA value) and the
   existing **`EOR #$80` sign-bias** for signed compares. Option A′ weighs
   *adding* the generic flag op on its merits.
2. Irie **always** fuses compare + `cf.cond_br` (the `i1` never materializes;
   `arith.select` for `cond ? a : b` is not even produced by the frontend — it
   lowers to a control-flow diamond + block-arg phi, see
   `Lit/.../control-flow/select.irie`). So any `arith.select` the legalizer
   emits for a wide compare would feed a branch and have to be **re-fused** back
   into a branch ladder in isel — re-deriving, in isel, exactly the ladder
   `SelectCmpIMultiByte` already emits today.

## What Irie does today (the thing Phase 3 would replace)

`SelectCmpIMultiByte` ([MOS6502InstructionSelector.cs]) already emits an
efficient high-to-low **CMP + branch ladder** directly, with `EOR #$80` sign
bias on the top bytes for signed `slt`. After Phase 2 it only ever sees the
canonical `{eq, uge, slt}`. It is correct, covered by golden tests
(`CmpI32Branch`, `CmpSignedI16/I32Branch`, `if-else`, `select`, …), and the
llvm-mos-reference aggregate sits at **+9.0%** with it.

So the realistic outcome of a faithful Phase 3 is: produce `arith.select` +
i8 `arith.cmpi` in the legalizer, then pattern-match them away in isel back into
the same ladder. **Net codegen is ~neutral; the win is purely architectural
(legalizer owns narrowing), and the risk of regression during re-fusion is real.**

## Option A — full migration (faithful to the parent plan) — **CHOSEN**

1. **Motivating consumer first (de-speculates the work).** Before any compare
   migration, add a real, *non-fused* `arith.select` consumer and its lit test,
   so the materialized-select path (step 4) is driven by something that actually
   needs it rather than by the fused wide-compare case (which re-fuses the select
   away and never materializes it). Good candidates, smallest first:
   - `min`/`max`: `%c = arith.cmpi slt, %a, %b; %r = arith.select %c, %a, %b;
     core.return %r` — the `cmpi` feeds a `select` (not a `cf.cond_br`) and the
     `select` result is returned, so **both** must materialize / fuse into a
     conditional-move-style sequence, not a branch to distinct blocks.
   - `abs`: `x <s 0 ? -x : x` (mirrors llvm-mos `legalizeAbs`,
     MOSLegalizerInfo.cpp:1431) — a `select` over two computed values.
   These are written directly as hand-authored MIR `.irie` tests (the frontend
   still lowers source-level `?:` to a CFG diamond, so the only way to exercise a
   true `arith.select` today is to author it in MIR). This test is the acceptance
   criterion for the materialized path and must pass before step 5 deletes the
   old ladder. It also answers the parent plan's "real need, not speculative"
   gate: the consumer is the need.
2. **Dialect ops.** Add to `arith`: `Select` (i1 cond, two same-width values),
   `Xor`, `And`, `Or`. Touch `ArithOp`, `ArithDialect` (`GetOpName`,
   `TryParseOp`, `GetInstructionInfo`, `IsSideEffectFree`), `MirBuilder`
   (`BuildSelect`/`BuildXor`/…), and the legalizer legality (`i8 = Legal`,
   `> 8 = NarrowScalar` for xor/and/or; `select` narrows element-wise).
   Writer/parser are generic and should round-trip without changes (add a
   round-trip lit test to confirm).
3. **Legalizer `LegalizeCmpI` wide path.** Replace "leave wide for the selector"
   with:
   - wide **eq** → OR-reduce the per-byte `xor`/`cmpi eq` to a single
     `i1`, *or* a lexicographic `select` of per-byte `cmpi eq` (match whichever
     re-fuses cleanly — see step 5).
   - wide **uge/slt** → lexicographic `select(eq(hi), cmp(rest), cmp(hi))`, with
     `slt` first applying `EOR #$80` (a new `arith.xor` with `0x80`) to the top
     bytes of both operands, then running the **uge** lexicographic form (keeps
     the Non-goal: no `G_SBC`, no V flag).
   - the artifact combiner folds the `unmerge`/`merge` round-trips, as in Phases
     1–2.
4. **isel `arith.select i8` (materialized path).** A select that does *not* feed a
   branch must lower to a real sequence. With no hardware select this is a small
   branch (`B<cc>` over two assignments into a common vreg) — which means emitting
   **new basic blocks** during isel, something the current
   `InstructionSelectorPass` does not do. This is the single largest unknown and
   the reason the parent plan flagged `arith.select` for its own sub-plan; the
   step-1 `min`/`max`/`abs` test is its acceptance criterion.
5. **isel re-fusion (the load-bearing part).** Teach the selector to recognize
   `cf.cond_br (arith.select … of arith.cmpi …)` and emit the existing CMP+branch
   ladder instead of materializing the select — the `selectBrCondImm` analog.
   Without this, every wide compare regresses to a materialized boolean +
   branch. This is essentially re-implementing `SelectCmpIMultiByte`'s ladder as a
   DAG matcher over `select`/`cmpi`.
6. **Delete** `SelectCmpIMultiByte` and now-unused helpers
   (`EorByteWithImmediate`, `FunnelThroughAnyi8`, `GetByteVregsOrUnmerge`).
7. **Verify** existing goldens unchanged (or improved), the new `arith.select`
   test passes, and the llvm-mos-reference aggregate is ≤ +9.0%.

**Cost/risk:** large; introduces block-creation in isel (step 3) and a DAG
re-fusion matcher (step 4) whose only job is to reproduce today's ladder. High
chance of touching RA/PhiElimination invariants. Codegen upside ≈ 0.

## Option B — resolve the divergence by design (recommended)

Conclude that Irie **intentionally** diverges from llvm-mos here, per the parent
plan's own Non-goals, and that the wide-compare branch ladder correctly lives in
isel because Irie fuses compare+branch and never materializes the `i1`:

1. Keep `SelectCmpIMultiByte` (the ladder) as the wide-compare lowering. It is
   the Irie analog of llvm's *post-re-fusion* `CmpBr*`, reached directly instead
   of via a select round-trip.
2. Update the parent plan's Phase 3 section and the relevant code comments to
   record this decision: "wide-compare narrowing stays in isel as a CMP+branch
   ladder with `EOR #$80` signed bias; the `G_SBC`/`select` data-flow form is a
   Non-goal." Tidy/rename for clarity if useful (e.g. note in
   `MOS6502LegalizerInfo` why wide cmpi is left wide).
3. Defer `arith.select`/`arith.xor` until a concrete consumer exists (e.g.
   front-end `cond ? a : b` over values that are *not* already a CFG diamond,
   `abs()`, or saturating ops). Add them then, with select lowering scoped
   against that consumer's actual shape rather than speculatively.

**Cost/risk:** minimal; documents intent; no code churn; no regression risk.

## Option A′ — generic subtract-with-flags op (the `G_SBC` analog), Non-goal relaxed

The parent plan's "no `G_SBC`" is **not** a hard rule; this option weighs adding
one on the merits. The appeal is **symmetry**: Irie already lowers wide *add* to
an `arith.addi_with_carry` chain, and already has `arith.subi_with_borrow`
(result + borrow-out). A compare is a subtract-discarding-result, so "wide
compare = `subi_with_borrow` chain, branch on the final flags" would unify
add/sub/compare narrowing under one chain primitive — the most uniform design on
paper.

Worked against Irie's specifics, it does not come out simpler:

1. **Multi-flag reuse is inherently physical.** The efficient ladder reuses one
   `CMP`'s physical flags for several branch decisions (`BCC` for `<` + `BNE` for
   `≠` per byte decides all three orderings). A generic op that yields one
   predicate/`i1` cannot express "one compare, two flag tests" without
   recomputing, so any generic data-flow form (select- or flag-SSA-based) loses
   the reuse and must be **re-fused** into the ladder at isel — which is most of
   the work and just re-derives `SelectCmpIMultiByte`.
2. **`subi_with_borrow` only yields the unsigned answer cleanly.** Final borrow =
   C = the `uge` result. But `eq` needs Z accumulated across all bytes and `slt`
   needs N⊕V of the final subtraction, so covering `{eq, uge, slt}` forces the op
   to grow N/V/Z outputs — i.e. a full flag-SSA `G_SBC`, a new IR concept
   (generic flag vregs needing a register class + physical-flag mapping), not a
   small extension to the existing op.
3. **`EOR #$80` is cheaper than N⊕V.** llvm uses N⊕V only because it commits to
   SBC-everywhere; it costs an extra SBC + conditional EOR + re-examine. Irie's
   sign bias is 2 EORs reusing the unsigned path. The "trick" we'd replace is the
   better code.

The generic-flag op pays off for llvm because llvm must materialize booleans in
general (compare results stored/returned/used as values). Irie never does
(i1-materialization is unsupported and the frontend lowers `?:` to a CFG
diamond), so the layer would be created in the legalizer and immediately
re-collapsed in isel. Verdict: **even with the Non-goal relaxed, this does not
make Irie simpler or its codegen better** — it adds an IR concept and a re-fusion
pass to reproduce today's ladder. Revisit only if Irie ever needs genuine
non-fused boolean compares (which would force materialization regardless).

## Decision

**Option A**, with the step-1 motivating `arith.select` consumer made a required
part of the work (decided 2026-06-17). The original scoping leaned toward Option
B because, for the *fused* wide-compare case alone, Option A builds machinery that
just re-derives today's ladder. The deciding factor is that we *do* want a true,
non-fused `arith.select` (a real consumer such as `min`/`max`/`abs`): once that
exists, the materialized-select path (step 4) is genuinely needed rather than
speculative, and migrating the wide compare to ride the same generic select
machinery becomes the consistent design rather than redundant work. Options B and
A′ remain on record above as the considered alternatives.

This also settles the two scoping questions that drove the original lean toward B:
- **A real `arith.select` consumer is in scope by decision** (step 1). It drives
  the design and de-speculates the materialized path.
- **Bare (non-branch) `i1` use is now an explicit goal**, via that consumer — so
  the materialized path (step 4) is necessary regardless of the wide-compare
  migration, and the wide migration rides the same machinery rather than
  justifying it alone.

## Remaining open questions for implementation

- **Block creation in isel (step 4).** The `InstructionSelectorPass` currently
  does not create basic blocks. Decide whether the materialized `arith.select`
  lowering creates them in isel, or whether a small pre-isel/target pass owns the
  select→diamond expansion (cf. how `PhiEliminationPass` already splits edges
  post-isel). This is the biggest design choice and should be settled before
  step 4.
- **`min`/`max` fusion vs. materialization.** For `cmpi` feeding a `select` whose
  result is returned, the ideal 6502 lowering is a compare + conditional load
  (a small branch), not a full boolean materialization. Decide whether step 4
  handles `select(cmpi …)` as a fused conditional-move pattern, with the
  fully-materialized flag→byte sequence as the fallback only for a select whose
  condition is an opaque `i1` (e.g. a function-argument bool).
