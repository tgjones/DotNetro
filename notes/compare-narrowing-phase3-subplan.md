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

**Two hard constraints make a faithful copy a non-goal for Irie:**

1. The parent plan's **Non-goals** forbid a `G_SBC`-style op that yields N/V/Z/C
   as generic SSA values: "Irie jumps straight from `arith.cmpi i8` to
   `mos6502.cmp` (physical flags); keep that. The legalizer narrows to
   `arith.cmpi i8`, not to a generic flag op." So Irie cannot use the `N⊕V`-via-V
   trick (no V as an SSA value) and must keep the existing **`EOR #$80` sign-bias**
   for signed compares.
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
