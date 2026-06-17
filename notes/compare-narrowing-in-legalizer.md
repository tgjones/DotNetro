# Plan: move integer-compare narrowing into the legalizer

## Motivation

Today, width-narrowing of integer operations is **split across two passes**:

- **Arithmetic** (`arith.addi` / `arith.subi`) narrows in the **legalizer**
  (`NarrowScalar` → a chain of `arith.addi_with_carry` / `arith.subi_with_borrow`).
- **Compares** (`arith.cmpi`) pass through the legalizer untouched and narrow in
  the **instruction selector** (`MOS6502InstructionSelector.SelectCmpIMultiByte`).

This is an inconsistency, not a considered design: the legalizer is the natural
home for "make every op a legal width", and isel should ideally pattern-match
already-narrow ops. The cost of the split showed up concretely while landing the
signed-compare-against-zero optimization (commit "Lower signed
compare-against-zero to a high-byte sign test"): the isel path had to re-implement
artifact handling the legalizer already does well — `GetByteVregsOrUnmerge`
peeking through `pseudo.merge`, plus a hand-rolled dead-merge DCE — and an early
version crashed RA with a `pseudo.merge` left dead by the rewrite. In the
legalizer, the `LegalizationArtifactCombiner` would have cleaned that up for free.

This plan mirrors **llvm-mos**, whose `MOSLegalizerInfo::legalizeICmp`
(`/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSLegalizerInfo.cpp:1218`)
does all compare *width + predicate* lowering, leaving the selector to do only
flag→branch fusion (`MOSInstructionSelector::selectBrCondImm`, same tree, line
967). It is **not** a goal to copy llvm's generic `G_SBC` flag-value modelling —
see "Non-goals".

## Current state (what exists, with anchors)

### The legalizer is already wired to key compares on operand width

`LegalizerPass.LegalizeInstruction` ([src/Irie/Passes/LegalizerPass.cs:261-310])
chooses the legality-query operand from the dialect's
`DialectInstructionInfo.TypeOperandIndex`, falling back to the first def. And
`ArithDialect.GetInstructionInfo` already sets
`ArithOp.CmpI => new DialectInstructionInfo(TypeOperandIndex: 2)`
([src/Irie/Dialects/Arith/ArithDialect.cs:46]) — i.e. cmpi's legality is queried
with **operand 2's type** (the `a` operand, e.g. `i16`), *not* the `i1` def.

> Note: the comment at [src/Irie/Target/MOS6502/MOS6502LegalizerInfo.cs:30-33]
> ("the type query here is always i1") is **stale** — `TypeOperandIndex: 2` means
> `GetAction(cmpi, i16)` already receives the wide operand type. Fixing that
> comment is part of phase 1.

So the only reason wide compares reach isel is that
`MOS6502LegalizerInfo.GetAction` returns `LegalityAction.Legal` for `CmpI`
unconditionally ([MOS6502LegalizerInfo.cs:33]). The `Custom` action path
(`LegalizeCustom`, dispatched at [LegalizerPass.cs:302-306]) already exists and is
used by `cast.trunc`.

### Where compares are lowered today (isel)

`MOS6502InstructionSelector` ([src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs]):

- `SelectCmpI` (line 516) — i8 path. Supports only `eq, ne, ult, uge`; throws
  `NotSupportedException` for signed and `ugt/ule`.
- `SelectCmpIMultiByte` (line 662) — i16/i32 path. Supports `eq, ne, ult, uge,
  slt, sge`. Signed compares use the `EOR #$80` sign-bias trick
  (`EorByteWithImmediate`, line 849) then run the unsigned byte chain.
- The signed-**against-zero** fast path (line ~720, guarded by
  `IsWideConstantZero`, line 901) — added by the prior commit; emits a single
  high-byte `CMP #0` + `BMI`/`BPL`. This is the one compare lowering that needs
  no select/and.
- Both paths **fuse** the following `cf.cond_br` into `mos6502.cmp` + `mos6502.bXX`
  + `mos6502.jmp.abs`, consuming the `i1` so it never reaches RA. Flags are
  modelled as physical registers (`$n/$v/$z`) here — correctly, since flags are
  physical.

### The dialect gap

`arith` has only `AddI, SubI, CmpI, AddICarry, SubIBorrow, Constant`
([src/Irie/Dialects/Arith/ArithOp.cs]). There is **no** generic `select`, `and`,
`or`, or `xor`. llvm's general (non-zero) compare lowering needs these:

- wide **unsigned/equality** compare → lexicographic
  `buildSelect(EqHigh, CmpRest, CmpHigh)` (llvm `legalizeICmp`, the
  `Pred != ICMP_SLT` branch, MOSLegalizerInfo.cpp:1282-1305).
- wide **signed non-zero** compare → multibyte `G_SBC` chain + `G_XOR #$80` +
  `G_SELECT` computing N⊕V (MOSLegalizerInfo.cpp:1318-1340).
  Concretely visible in `ext/llvm-mos-reference/control-flow/loop-counter.txt`
  around the `G_SELECT %96, %99, %93` line (the `i < n` compare).

## Reference: how llvm-mos splits the work

1. **Predicate normalization** (legalizer, MOSLegalizerInfo.cpp:1232-1244):
   every predicate is reduced to `EQ`, `UGE`, or `SLT` via negate/swap.
2. **Width narrowing** (legalizer, custom `legalizeICmp`):
   - `EQ` vs 0 feeding only branches → `G_CMPZ` (multibyte OR-to-zero).
   - `SLT` vs **0** → rewrite to i8 `slt highByte, 0` (operand rewrite; no select).
   - other wide compares → lexicographic `buildSelect`, or the signed SBC chain.
3. **Flag→branch fusion** (selector, `selectBrCondImm` + the `m_CmpNZ*`
   matchers, MOSInstructionSelector.cpp:967+): the narrow flag-producing op + a
   `G_BRCOND_IMM` collapse into one `CmpBr*` (e.g. `CMPImm $x, 0; BR $n`).

The key takeaway: **even in llvm the fusion stays in the selector.** Moving
narrowing to the legalizer does not consolidate compares into one place — it
re-divides the work as *legalizer = width + predicate*, *isel = flag fusion*.

## Target architecture for Irie (pragmatic)

- **Legalizer** owns `arith.cmpi` width-narrowing (via `Custom`) and predicate
  normalization. Output: narrow `arith.cmpi i8` instructions (plus, for the
  general case, `arith.select` / `arith.xor` on i8/i1).
- **Isel** keeps only `arith.cmpi i8 + cf.cond_br → mos6502.cmp + mos6502.bXX`.
  All physical-flag modelling stays here.

## Non-goals

- Do **not** introduce a generic `G_SBC`-style op that produces N/V/Z/C as
  generic SSA values. Irie jumps straight from `arith.cmpi i8` to `mos6502.cmp`
  (physical flags); keep that. The legalizer narrows to `arith.cmpi i8`, not to a
  generic flag op.
- Do not change emitted code in phase 1/2 — those are structural/coverage
  refactors. Output should be identical (verified by the golden lit `CHECK`s).

## Incremental phases

### Phase 1 — move the zero-RHS signed compare to the legalizer (no prereqs)

This is the slice that needs **none** of the missing dialect ops, and lets us
delete the bespoke isel machinery.

1. In `MOS6502LegalizerInfo.GetAction`, return `Custom` (not `Legal`) for
   `ArithOp.CmpI` when the queried operand type width > 8. Fix the stale comment.
2. In `MOS6502LegalizerInfo.LegalizeCustom`, add a `cmpi` case. For phase 1,
   handle only: signed predicate (`slt`/`sge`) with a statically-zero RHS →
   `buildUnmerge` the LHS, rewrite the instruction in place to
   `arith.cmpi <pred>, lhsHigh, (i8)0`. Mirrors MOSLegalizerInfo.cpp:1308-1318.
   Let the artifact combiner clean up the merges/unmerges.
3. Teach `SelectCmpI` (the i8 path) to handle `slt`/`sge` against an i8 `0`:
   emit `mos6502.cmp %a, #0` + `BMI`/`BPL`. (Lift the body of the current isel
   fast path down to the i8 path.)
4. Delete the zero-RHS fast path and `IsWideConstantZero` from
   `SelectCmpIMultiByte`; the wide case no longer reaches isel for this shape.
5. Regenerate `early-return.irie`'s golden `CHECK` (output must be **unchanged**
   — this is the proof phase 1 is structure-only).

**Caveat to resolve in phase 1:** `SelectCmpIMultiByte` still owns the *general*
wide-signed (`EOR #$80`) and wide-unsigned chains. Returning `Custom` for *all*
wide cmpi means the legalizer must hand those back in a shape isel still accepts.
Two options:
- (a) Phase-1 `legalizeICmp` only transforms the zero-signed shape and leaves all
  other wide cmpi **as wide `arith.cmpi`**, returning without change — but then
  `Custom` must be a no-op for them and isel keeps its wide path. Cleanest if
  `LegalizeCustom` is allowed to leave an op wide.
- (b) Keep `GetAction` returning `Legal` for the shapes isel still handles, and
  only `Custom` for the zero-signed shape. Requires `GetAction` to inspect the
  RHS/predicate, which it currently can't (it only sees a type). Prefer (a).

Confirm `LegalizerPass` tolerates a `Custom` legalization that leaves the op at a
still-illegal width without re-queuing infinitely ([LegalizerPass.cs:302-306]).
If it re-queues, phase 1 needs option (b) or a guard.

### Phase 2 — predicate normalization in the legalizer

1. Add a normalization step (legalizer custom, or a small pre-pass) reducing the
   10 predicates to `eq/uge/slt` via negate (`ne/ult/sge`) and swap
   (`ule/ugt/sle/sgt`). Mirrors MOSLegalizerInfo.cpp:1232-1244.
2. Strip the now-dead predicate cases and `NotSupportedException`s from
   `SelectCmpI` / `SelectCmpIMultiByte`; the selector now only sees `eq/uge/slt`.
3. **This closes a real coverage gap** — `ugt/ule/sgt/sle` and i8-signed are
   currently unsupported in isel.
4. New lit tests for each previously-unsupported predicate (i8 and i16).

Negation flips the branch sense, so the `cf.cond_br` true/false targets swap when
a predicate is negated — handle in the normalization, not isel.

### Phase 3 — general non-zero compares (needs new dialect ops)

**Prerequisite:** add generic ops to the `arith` dialect:
`arith.select` (i1 cond, two same-width values), and `arith.xor` (and likely
`arith.and` / `arith.or` for completeness). Update `ArithOp`, `ArithDialect`
(names, `GetInstructionInfo`), the parser/writer, legalizer legality (i8 = Legal),
and isel (`arith.select i8` → `mos6502`… ; `arith.xor i8` → `mos6502.eor`).

Then:
1. In `legalizeICmp`, implement the wide **unsigned/eq** lexicographic path
   (`buildSelect(EqHigh, CmpRest, CmpHigh)`) and the wide **signed non-zero** SBC
   path (N⊕V via xor+select). Mirrors MOSLegalizerInfo.cpp:1282-1340.
2. Delete `SelectCmpIMultiByte` and its helpers (`EorByteWithImmediate`,
   `FunnelThroughAnyi8`, `GetByteVregsOrUnmerge` if now unused). isel keeps only
   the i8 path + fusion.
3. This is also where the separate "Irie uses `EOR #$80`, llvm uses an SBC chain"
   divergence (flagged during the early-return work) gets resolved, since the
   generic SBC-style lowering lives here.

## Verification

- After each phase: `dotnet test --solution src` (Debug **and** Release — CI runs
  both), and `dotnet run -c Release --project src/Irie.Tools.Reference` to confirm
  the llvm-mos-reference aggregate does not regress (currently +9.0%).
- Phases 1–2 must leave **emitted code unchanged** (golden `CHECK`s prove it).
  Only phase 3 may change output (and should *improve* the general signed-compare
  cases — track via the report).
- The lit suites are the regression net: `Irie.Tests` (incl. every `.irie`/`.s`
  lit test) and `DotNetro.Compiler.Tests` (the real compiler pipeline, which
  exercises the same selector).

## Risks / open questions

- **Can `LegalizeCustom` leave an op wide?** Determines phase-1 option (a) vs (b).
  Verify against `LegalizerPass` worklist behaviour before starting.
- **`arith.select` lowering on 6502** is non-trivial (no hardware select) — it
  becomes a small branch or a flag-driven sequence. Scope this when phase 3
  starts; it may be worth its own sub-plan.
- **Sequencing:** phase 1 is worth doing on its own merits (deletes the bespoke
  merge-DCE, makes the layering consistent). Phase 2 is high-value (coverage) and
  cheap. Phase 3 is the largest and should be triggered by either a real need for
  the missing predicates/general signed compares, or the codegen-quality work on
  the `EOR #$80` → SBC divergence — not done speculatively.
