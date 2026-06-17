# Sub-plan: Phase 3 of compare-narrowing (wide non-zero compares + `arith.select`)

Status: **draft for review — not yet approved or started.**
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

## Option A — full migration (faithful to the parent plan)

1. **Dialect ops.** Add to `arith`: `Select` (i1 cond, two same-width values),
   `Xor`, `And`, `Or`. Touch `ArithOp`, `ArithDialect` (`GetOpName`,
   `TryParseOp`, `GetInstructionInfo`, `IsSideEffectFree`), `MirBuilder`
   (`BuildSelect`/`BuildXor`/…), and the legalizer legality (`i8 = Legal`,
   `> 8 = NarrowScalar` for xor/and/or; `select` narrows element-wise).
   Writer/parser are generic and should round-trip without changes (add a
   round-trip lit test to confirm).
2. **Legalizer `LegalizeCmpI` wide path.** Replace "leave wide for the selector"
   with:
   - wide **eq** → OR-reduce the per-byte `xor`/`cmpi eq` to a single
     `i1`, *or* a lexicographic `select` of per-byte `cmpi eq` (match whichever
     re-fuses cleanly — see step 4).
   - wide **uge/slt** → lexicographic `select(eq(hi), cmp(rest), cmp(hi))`, with
     `slt` first applying `EOR #$80` (a new `arith.xor` with `0x80`) to the top
     bytes of both operands, then running the **uge** lexicographic form (keeps
     the Non-goal: no `G_SBC`, no V flag).
   - the artifact combiner folds the `unmerge`/`merge` round-trips, as in Phases
     1–2.
3. **isel `arith.select i8` (materialized fallback).** A select that does *not*
   feed a branch must lower to a real sequence. With no hardware select this is a
   small branch (`B<cc>` over two assignments into a common vreg) — which means
   emitting **new basic blocks** during isel, something the current
   `InstructionSelectorPass` does not do. This is the single largest unknown and
   the reason the parent plan flagged `arith.select` for its own sub-plan.
4. **isel re-fusion (the load-bearing part).** Teach the selector to recognize
   `cf.cond_br (arith.select … of arith.cmpi …)` and emit the existing CMP+branch
   ladder instead of materializing the select — the `selectBrCondImm` analog.
   Without this, every wide compare regresses to a materialized boolean +
   branch. This is essentially re-implementing `SelectCmpIMultiByte`'s ladder as a
   DAG matcher over `select`/`cmpi`.
5. **Delete** `SelectCmpIMultiByte` and now-unused helpers
   (`EorByteWithImmediate`, `FunnelThroughAnyi8`, `GetByteVregsOrUnmerge`).
6. **Verify** golden goldens unchanged (or improved) and the aggregate ≤ +9.0%.

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

## Recommendation

**Option B.** Phases 1–2 already delivered the parent plan's concrete wins
(consistent signed-vs-zero narrowing, full predicate coverage, deletion of the
bespoke isel merge-DCE). Phase 3's remaining delta is architectural-consistency
only, it is blocked on `arith.select` (whose isel lowering needs block creation),
and it would replace a working, efficient ladder with machinery that re-derives
that same ladder — against the parent plan's "not speculatively" guidance and the
`G_SBC` Non-goal. Take Option B now; revisit a scoped `arith.select` when a real
consumer lands.

## Open questions for the reviewer

- Is there a near-term front-end need for a true `arith.select` (non-CFG
  `cond ? a : b`, `abs`, min/max, saturating arithmetic)? If yes, that consumer
  should drive the `arith.select` design, and the wide-compare migration can ride
  along; if no, Option B.
- Do we want the wide unsigned/eq lexicographic-`select` form for any reason
  other than the signed case (e.g. a future non-fused boolean compare)? If bare
  (non-branch) `i1` compares ever need to be supported, Option A's materialized
  path becomes necessary regardless.
