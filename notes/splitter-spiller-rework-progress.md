# Progress: SplitKit-style splitter + spiller rework

Companion to [`splitter-spiller-rework-plan.md`](splitter-spiller-rework-plan.md).
Records the per-stage findings as the rework lands.

---

## Stage R0 — characterization (DONE)

Instrumentation added (OFF by default; see "Instrumentation" below) and the
convergence check run on the four required cases with the greedy default flipped
on, the three adc/sbc isel out-funnels disabled, and
`InsertRelocationCopiesForConstrainedDefs` disabled. All four **fail to converge**
under greedy-with-funnels-off, in two distinct failure modes. The convergence-
check flips were reverted afterwards; the trace instrumentation stays.

### How the four cases were driven

The four inputs are lit MIR files under
`src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/`:
`basics/add-i16.irie`, `basics/add-i32.irie`,
`control-flow/common-subexpr.irie`, `control-flow/early-return.irie`. They were
run directly through `iriec --target mos6502 --emit=asm <file>` with
`IRIE_RA_TRACE=1` after the (temporary) convergence-check flips were built into
the library.

### The two failure modes

**Mode A — straight-line chains spill the final constrained adc result**
(`add_i16`, `add_i32`).

**Mode B — control-flow cases loop forever at the split rung, never reaching
spill** (`common_subexpr`, `early_return`).

### Per-case precise cause

#### add_i16 — Mode A (spill loop)

Post-isel-with-funnels-off, the i16 chain is two two-address adcs threading the
carry. Round-by-round:

- **round 1**: assign `%3→$a`, `%19→$a` (the first adc's result); `%21` (second
  adc's result, pinned `{$a}`) fails → instruction-split fires (mints a copy
  before the tied use).
- **round 2**: `%19→$a` again; `%21` fails again → incumbent-across-clobber split
  fires, relocating the incumbent `%19` into a fresh flexible temp `%24`
  (`%24 : any8 = pseudo.copy %19`), with `%19`'s later use (`$0 = copy %19`)
  redirected to `%24`.
- **round 3**: `%24→$a`, then `%21` SPILLs. Trace:
  `%21 allowed={0} stage=Split segments=[17..19,21..25]`.

**Cause.** The incumbent relocation temp `%24` is minted in the flexible `any8`
class, but RA's **copy-hint preference** (`GreedyRegisterAllocator.PreferenceOrder`
→ `CopyHintColours`) puts `$a` FIRST in `%24`'s preference, because `%24`'s only
source is `%19` which was on `$a` — collapsing the relocation copy is the hint's
whole point. So RA assigns `%24 → $a`. `%24` is live across `%21`'s def (it
carries the low result to `$0 = copy %24` near the end), so it occupies `$a`
exactly where the second adc result `%21` needs it. `%21`'s allowed set is `{$a}`
ONLY (forced by the adc def[0] operand class `Ac`; see
`MOS6502Dialect.AdcInfo`), it is at `stage=Split` (so eviction is disabled by the
`stage != Split` guard in `Run`), and no split kind applies to it (it is the
re-clobber, not a value live across a later `{$a}` def), so it spills. Store/reload
of a `{$a}`-only value mints a reload temp that is again `{$a}`, which does not
relieve the contention — `%21` re-spills every round to the round cap.

The relocation **did not move the value off `$a`** — it renamed it (`%19`→`%24`)
and RA's copy-hint parked the rename right back on `$a`.

#### add_i32 — Mode A (spill loop)

Identical mechanism, one more byte. The 4-byte chain's relocation temps
(`%46` from `%33`, `%42` from `%35`, `%44` from `%37`) are all minted flexible
but copy-hinted back onto `$a`/etc.; the final adc result `%39` (allowed `{$a}`,
`segments=[41..43,45..53]`) cannot be placed because a relocation temp is live on
`$a` across `%39`'s def, and `%39` is at `stage=Split` so it cannot evict and no
split kind applies → SPILL `%39 (class any8)` at round 7, then re-spills to the
cap. The printed `class any8` is the annotation; the **allowed set is `{$a}`**.

#### common_subexpr — Mode B (infinite split loop)

The shared `a+b` (computed once in bb0 by HoistCommonCodePass) is the two-adc
i16 value whose high byte is `%72` (second adc result, pinned `{$a}`). `%72` is
used **cross-block**: `%86 = copy %72` in bb1 (store to g1+1) and
`%87 = copy %72` in bb2 (store to g2+1). Both arms contain their own `{$a}`-pinned
adc/sbc results.

Every round `TrySplitConstrainedDefResult` fires on `%72`
(`split %72 (kind constrained-def-result)`), forever. Comparing round 2 → round 3
post-split IR, each round inserts ANOTHER copy in bb0 and redirects the previous
temp:

```
round 2 bb0:  %88 : any8 = pseudo.copy %72
round 3 bb0:  %89 : any8 = pseudo.copy %72
              %88 : any8 = pseudo.copy %89
```

**Cause.** `FindLaterReClobber` scans **all** blocks, so it finds the bb1/bb2
adc/sbc defs (also `{$a}`-pinned) that `%72` is live across via its cross-block
uses. But `RelocateAcrossClobber`'s redirect loop is **block-local** — it only
rewrites uses within `%72`'s defining block (bb0). So the cross-block uses
(`%86`/`%87` in bb1/bb2) keep `%72` live across the re-clobbers, the donor range
NEVER shrinks, and the split fires again next round, inserting one more copy.
This is a true non-termination at the split rung (the well-founded measure the
plan asks for in R2 — "donor range strictly shrinks each split" — is violated).
It hits the round cap before ever reaching the spill rung.

#### early_return — Mode B (infinite split loop)

Same as common_subexpr. The shared `a+b` (hoisted into bb4) has high byte `%63`
(second adc result, pinned `{$a}`), used cross-block at `%21 = copy %63` (bb5) and
`%69 = copy %63` (bb6); bb6 contains its own `{$a}`-pinned adc results
(`%67`/`%69`). `constrained-def-result` split fires on `%63` every round, each
inserting one more bb4-local copy (round 2: `%72 = copy %63`; round 3:
`%73 = copy %63` + `%72 = copy %73`), while the cross-block uses keep `%63` live
across the bb6 re-clobbers. Donor range never shrinks → infinite split loop, hits
the round cap.

### Layer-4 answer (definitive)

The plan's layer-4 question: *why does greedy spill an `any8` value (full ~30-reg
zero-page pool) that should never need to spill — is interference over-conservative
after the heuristic split, or is there an assign/interference bug?*

**Answer: neither.** Two findings:

1. **The "flexible any8 value" is not flexible at the spill point.** The value
   that spills (`%21`/`%39`) prints as `class any8` (its annotation) but its
   **allowed set is `{$a}` only** — a single physreg. `ComputeAllowedColours`
   intersects the `any8` annotation down to `{$a}` because the value is the
   tied-def of `mos6502.adc`, whose def[0] operand class is `Ac` (and it is
   "touched by a non-copy op", so the annotation is NOT widened). The trace
   confirms it: `%21 allowed={0}` (`0` = `$a`). So the premise "full pool, should
   never spill" is based on the printed annotation, not the actual allowed set;
   the value is genuinely hard-pinned to `$a`.

2. **The interference RA measures is CORRECT; the heuristic edit is what's
   wrong.** Fresh per-round reanalysis is accurate — it is not over-conservative
   or stale. The bug is that the block-local heuristic relocation edit does not
   actually push the value off the constrained register:
   - **Mode A**: the relocation temp is minted flexible but RA's copy-hint
     preference assigns it right back to `$a` (collapsing the relocation copy),
     so the value never leaves `$a` and the constrained final result has nowhere
     to go. If the temp were forced off `$a` (e.g. into a zp register), the final
     adc result would assign `$a` and there would be no spill — the interference
     is real and resolvable, just mis-placed by the edit.
   - **Mode B**: the relocation edit is block-local but the conflicting
     re-clobber is in a different block reached by the value's cross-block uses,
     so the edit cannot shorten the donor range at all — it loops at the split
     rung forever, never reaching spill.

This directly grounds the plan's R1/R2 design choices:
- R1 (slot-precise SplitEditor that strictly shrinks the donor range, handles
  multi-def/two-address by construction) must redirect **all** uses in the
  across-clobber sub-range, **including cross-block uses**, or guard the
  constrained-def-result split to only fire when the conflict and uses are in the
  same block (Mode B fix — the donor range must actually shrink).
- R2 (cost model + split-not-spill) must ensure the relocated temp does NOT land
  back on the constrained register — either suppress the copy-hint toward the
  clobbered register for a relocation temp, or constrain the temp away from it via
  interference, mirroring llvm-mos forcing the across-clobber piece to a different
  register through the clobber interference window (Mode A fix).

### Instrumentation (added; stays; OFF by default)

New file `src/Irie/Passes/RaTrace.cs` — a gated trace facility keyed on the
`IRIE_RA_TRACE` environment variable (unset / empty / `0` / `false` = off; any
other value = on). All output goes to **stderr** so it never pollutes the
`--emit=…` stdout stream the lit harness diffs. With trace off, every method
short-circuits and nothing is written; trace-on and trace-off allocate
identically (the trace only observes).

Wiring in `GreedyRegisterAllocator`:
- constructor takes a `round` arg (threaded from the pass's spill loop, label
  only — no effect on allocation);
- `Commit` emits `assign %v -> $reg`;
- `Run` step 4 emits `split %v (kind …)` plus a one-shot post-split full-function
  MIR dump (`post-split IR:`);
- `Run` step 5 emits `SPILL %v (class …)` plus a diagnostic note with the value's
  allowed-register set, stage, and live-range segments.

`RegisterAllocatorPass` passes its `round` counter into the greedy engine.

Enable with:

```
IRIE_RA_TRACE=1 dotnet src/Irie.Tools.Compiler/bin/Debug/net10.0/iriec.dll \
  --target mos6502 --emit=asm <file.irie>     # trace → stderr
```

### Validation

- `dotnet test --project src/Irie.Tests` — **196/196 green** after reverting the
  convergence-check flips (with the trace instrumentation in place, off).
- Convergence-check flips (greedy default → true, three adc/sbc out-funnels
  disabled, `InsertRelocationCopiesForConstrainedDefs` disabled) are **all
  reverted**; `grep -rn CONVERGENCE-CHECK src/Irie` returns nothing, and the only
  remaining source diff is the `round` threading + `RaTrace.cs`.

---

## Stage R1 — slot-precise SplitEditor (DONE)

Re-expressed the relocation edit in `RelocateAcrossClobber`
(`src/Irie/Passes/SplitEditor.cs`) in terms of SlotIndexes and multi-def
(two-address) vregs, and added a single principled across-clobber correctness
exclusion to the greedy allocator. Both changes are greedy-engine-only — the
default colourer path is untouched. Convergence-check flips were applied,
measured, and **reverted**; the only committed source diff is `SplitEditor.cs`,
`GreedyRegisterAllocator.cs`, and the new unit test.

### What changed

1. **Slot-precise, cross-block relocation** (`SplitEditor.RelocateAcrossClobber`).
   - The producing-def selection (Stage 4b-i) was already slot-precise — verified
     and kept: the donor's value is the one produced by `vreg`'s **latest def
     before the clobber** (`producingDefSlot`), found via `DefSlot` order across
     all blocks, not `GetDefinition`'s first def.
   - The **redirect loop is now cross-block and slot-keyed**. It collects, before
     the edit, every use of `vreg` whose `UseSlot` lies in
     `(producingDefSlot, nextDefSlot)` across **all** blocks (`nextDefSlot` = the
     earliest later def of `vreg`, or +∞, so a subsequent value is untouched), then
     redirects them to the flexible temp. Because `LiveIntervals` numbers slots in
     program order across every block, this naturally reaches the cross-block uses
     the old block-local loop left behind — the Stage-R0 **Mode-B** cause (the
     donor range never shrank because its cross-block uses kept it live across the
     re-clobbers). Operating on recorded `(instr, index)` pairs makes the rewrite
     index-shift-proof against the inserted copy.
   - **Strict-shrink assertion (well-founded measure).** The edit now records
     whether any redirected use is at/after the clobber point; if none is, it
     throws (`relocation … redirected no across-clobber use`) rather than looping to
     the round cap. So a non-shrinking edit fails loudly at its cause.

2. **Across-clobber correctness exclusion**
   (`GreedyRegisterAllocator.ExcludeHardClaimedAcrossRegisters`, called once per
   `Run`). A value W with allowed == {R} hard-claims R at its def-point; any other
   value V whose live range **covers** W's def-point (V is live across that
   re-clobber of R) must not be assigned R. We remove R from V's allowed set,
   guarded so V is never emptied (a genuinely {R}-only V's conflict is left for the
   spill rung). This is the sanctioned R1 correctness fix named in the task: "a
   value live across a clobber of register R cannot be assigned R." It kills the
   Mode-A copy-hint hazard where the relocation temp landed **back** on $a (the
   trace confirms: with the exclusion, the temp `%24` now assigns `$zp2`, off $a).
   The interference between V and W was always exact; this is a preference/allowed
   narrowing that removes the ordering hazard (V grabbing R before W is processed),
   mirroring llvm-mos forcing the across-clobber piece off R through the clobber
   interference window.

### Unit test

`GreedyRegisterAllocatorTests.ConstrainedDefLiveAcrossReClobber_CrossBlockUse_RedirectsAndConverges`
— a hand-built two-block fixture: `v0` (`ac`, non-copy def) is live across `v1`'s
$a re-clobber in bb0 AND used in bb1. Asserts (1) `Pass.Run` does not throw
(the old block-local edit tripped the round cap), (2) no `pseudo.spill` is
emitted, (3) `v1` keeps $a while the bb1 use of `v0` reads a non-$a (zero-page)
register — proving the cross-block redirect. Verified to **fail** (round-cap
throw) with the Stage-R1 `SplitEditor` change reverted, and pass with it.

### Convergence results (greedy default + funnels-off + relocation-prepass-off)

| case | mode | R0 result | R1 result |
|------|------|-----------|-----------|
| `common_subexpr` | B | infinite split loop (round cap) | **converges, 0 spills** |
| `early_return`   | B | infinite split loop (round cap) | **converges, 0 spills** |
| `add_i16`        | A | spill loop (round cap)          | bounded; single `pseudo.spill` survives → **needs R2** |
| `add_i32`        | A | spill loop (round cap)          | bounded; single `pseudo.spill` survives → **needs R2** |
| `sub_i16`        | A | spill loop (round cap)          | bounded; single `pseudo.spill` survives → **needs R2** |

**Mode B is fully fixed by R1** — both control-flow cases now converge to a valid
allocation with no spill (each constrained-def-result split fires a bounded number
of times because the donor range strictly shrinks every round, instead of forever).

**The chains (Mode A) still need R2.** The plan expected the chains to converge in
R1, but the R0 boundary note was correct: after the relocation temp is forced off
$a (the new exclusion does this — confirmed in the trace), the chains spill a
**different** value than R0 reported. The remaining spill is the second adc's
result vreg (`%21`/`%39`, allowed = {$a}), whose live range now has a **first
segment over the tied-accumulator copy** (`%21 = pseudo.copy %4` at the top of the
chain) that conflicts with the **first** adc's $a occupancy. Trace (`add_i16`,
round 3): `SPILL %21 allowed={0} segments=[17..19,21..25]` — segment `[17..19]` is
the tied-acc copy, where $a is held by the first chain link. This is **not** a
relocation-temp problem (R1 fixed that); it is that the accumulator-side value
(`%4` → `%21`'s tied-acc copy) is pinned to $a too early — it should be held in a
GPR (`$x`/`$y`) and moved to $a (`txa`/`tya`) only at the adc, which is exactly the
llvm-mos `tay/txa/adc/tax/tya` shape. Reconstructing that requires either
split-not-spill for the tied-accumulator copy chain or a cost model that prefers
splitting the accumulator side over spilling the result — **R2** (cost model +
split-not-spill + terminating stage ladder), not a small correctness fix.
**Stopped at the R1/R2 boundary as instructed.**

### Validation

- `dotnet test --project src/Irie.Tests` — **197/197 green** (196 + the new R1
  unit test) with the convergence-check flips reverted.
- `dotnet test --project src/DotNetro.Compiler.Tests` — **21/21 green**.
- Convergence-check flips (greedy default → true, three adc/sbc out-funnels
  disabled — replaced by a direct `ReplaceAllUsesOfRegister(resultVreg, newResult)`;
  `InsertRelocationCopiesForConstrainedDefs` disabled) are **all reverted**:
  `grep -rn CONVERGENCE-CHECK` returns nothing; the only committed diff is
  `SplitEditor.cs`, `GreedyRegisterAllocator.cs`, and the new unit test. Greedy
  stays flag-gated (`useGreedy: false` default); the colourer remains the default
  engine and its goldens are unchanged.

---

## Stage R2 — cost model + split-not-spill + terminating stage ladder (DONE)

Closed the Mode-A residual spill: all five corpus cases now converge with NO
spill under greedy-with-funnels-off, and the i16 chains (`add_i16`, `sub_i16`)
reconstruct the exact llvm-mos `tay`/`tya` relocation shape. Greedy-engine-only —
the default colourer path is byte-for-byte unaffected (goldens untouched).

### Root cause of the R1 residual spill (precise)

The pre-RA two-address i16 chain (post-funnel-off) is, for `add_i16`:

```
%19 = adc %19, %5, %18        ; LOW result, ac ($a), live across the next adc
%21 = adc %21, %6, %20        ; HIGH result, ac ($a)
$a  = pseudo.copy %19         ; ABI return move, LOW byte  (emitted LSB-first)
$x  = pseudo.copy %21         ; ABI return move, HIGH byte
```

R1's incumbent-split relocates `%19` (low) off `$a`. The residual spill is `%21`
(high, allowed `{$a}`). Its interval overlaps `$a`'s **precoloured busy window**:
`MOS6502CallLowering.LowerReturn` emits the per-byte return copies **LSB-first**,
so `$a = pseudo.copy <low>` (a DIRECT physreg def of `$a`) is scheduled BEFORE
`$x = pseudo.copy %21`, clobbering `$a` while `%21` is still needed for its own
return move. `FindLaterReClobber` did not catch this — the re-clobber is a
precoloured `$a` def, not another `{$a}`-pinned vreg — so `%21` exhausted assign /
evict / every split kind and SPILLed (re-spilling to the round cap, since a
`{$a}`-only reload temp does not relieve the contention). The default colourer
never hits this because the isel out-funnel relocates BOTH adc results off `$a`
before RA; greedy (funnels off) must reconstruct that relocation on demand.

The llvm-mos reference confirms the target shape: `clang --target=mos -Os` emits
`clc / adc __rc2 / tay / txa / adc __rc3 / tax / tya / rts` — the low result is
held in `$y` across the high-byte adc (`tay`), moved back to `$a` for the return
(`tya`); the high result is moved to `$x` (`tax`). Pre-RA MIR
(`llc -stop-before=greedy`) shows the high accumulator `%15:ac = COPY $x` two-
address, and the **return copies in HIGH-then-LOW order** (`$x = COPY %15` then
`$a = COPY %13`).

### What changed (greedy-engine-only; all flag-gated)

1. **Split-not-spill across a precoloured physreg clobber** —
   `SplitEditor.TrySplitRelocatableAcrossPhysClobber`. A `{R}`-pinned,
   flexible-superclass value live across a DIRECT precoloured def of R (the
   LSB-first return move clobbering `$a`) is relocated off R via the existing
   `RelocateAcrossClobber` edit, keyed off R's precoloured busy interval rather
   than a vreg re-clobber. This is the llvm-mos `RS_LightSpill` analogue ("spill
   to a wider register class to avoid spilling to the stack"). Wired into
   `GreedyRegisterAllocator.TrySplit` after the vreg-re-clobber kinds. `%21`
   relocates instead of spilling. (`SplitEditor.cs`, `GreedyRegisterAllocator.cs`
   `TrySplit`.)

2. **Tied-use exclusion in the instruction-split** —
   `SplitEditor.CollectNarrowingUses` now skips operands tied to a def
   (`GetTiedToIndex(k) >= 0`). Routing a tied use through a copy buys nothing (the
   tie re-pins the fresh temp to the def's class) and only grows the range; the
   tied-accumulator copy of a two-address adc/sbc is exactly this futile case, and
   it was the spurious round-1 split that fought the well-founded measure. The
   relocation kinds handle that shape. (`SplitEditor.cs`.)

3. **Split-product GPR preference** (the tay/tya shape) —
   `GreedyRegisterAllocator.PreferenceOrder`. A relocation split product prefers
   the target's cheap-transfer real GPRs (`GetShortRangeGprPreference()` →
   `$x/$y/$a`) ahead of the abundant zero-page pool. This is the copy-cost
   preference of llvm-mos `getRegAllocationHints`: a relocation escaping `$a`
   (`%t = copy <$a value>` … `$a = copy %t`) is cheapest in a GPR (`tay`/`tya`,
   1 byte / 2 cycles) and dearer in zp (`sta`/`lda`, 2 bytes / 3 cycles). Applied
   ONLY to split products (the general flexible-value placement still favours zp to
   keep the GPRs free for arithmetic). Without it the chains converge but emit
   `sta`/`lda`; with it they emit `tay`/`tya`, matching the reference exactly.
   (`GreedyRegisterAllocator.cs`.)

4. **Eviction-by-cost** — `GreedyRegisterAllocator.TryGatherEvictees` + the new
   `CanReassign`. A faithful port of `canEvictInterferenceBasedOnCost`'s
   preemptive path: a failing value with ONE allowed register may evict a
   REASSIGNABLE incumbent regardless of spill weight (the only way it places),
   provided the incumbent can move to another free register. Cascade
   loop-prevention unchanged. **Not exercised by the corpus** (the R1 hard-claim
   exclusion + the split rungs already resolve every case — verified by a
   one-shot `BYCOST` probe over all five), but the plan lists it as R2 work item 1
   and it is the documented reference behaviour; kept as a tested cost-model
   completion (see the isolating unit test). (`GreedyRegisterAllocator.cs`.)

5. **Terminating stage ladder + well-founded measure** —
   `GreedyRegisterAllocator.ConstrainedRangeMeasure` /
   `ComputeConstrainedRangeMeasure` / `SpansClobberOfPinnedReg`, asserted in
   `RegisterAllocatorPass`. The measure is the COUNT of constrained,
   non-split-product vregs still live across a clobber of their single pinned
   register — exactly the set of across-clobber relocations still to perform. Each
   RELOCATION split clears one such span, so the count strictly decreases and is
   bounded below by zero. It is **insertion-invariant** (a raw slot-length total is
   NOT — inserting a relocation copy shifts all slot numbers — which is why the
   first attempt false-positived). Policed only on relocation-split rounds
   (`LastSplitWasRelocation`); the other split kinds terminate by their own
   construction (`_splitProducts` are never re-split). A non-shrinking relocation
   now throws at its cause, not at the round cap. (`GreedyRegisterAllocator.cs`,
   `RegisterAllocatorPass.cs`.)

### Convergence results (greedy default + funnels-off + relocation-prepass-off)

| case | R1 result | R2 result |
|------|-----------|-----------|
| `common_subexpr` | converges, 0 spills | converges, 0 spills |
| `early_return`   | converges, 0 spills | converges, 0 spills |
| `add_i16`        | 1 residual `pseudo.spill` | **converges, 0 spills — `CLC / ADC $02 / TAY / TXA / ADC $03 / TAX / TYA / RTS` (matches llvm-mos exactly)** |
| `sub_i16`        | 1 residual `pseudo.spill` | **converges, 0 spills — `SEC / SBC $02 / TAY / TXA / SBC $03 / TAX / TYA / RTS` (matches llvm-mos exactly)** |
| `add_i32`        | 1 residual `pseudo.spill` | **converges, 0 spills — `tay`/`tya` on the bytes with a free GPR, zp on the rest (a genuine 4-byte chain; only 3 GPRs exist, so the middle bytes must use zp — correct)** |

The whole Irie lit suite, run under the convergence-check flips, has **zero**
`did not converge` / `pseudo.spill survived` / `non-terminating split` crashes —
greedy-with-funnels-off now terminates for every function in the corpus, not just
the five.

### Unit tests added (`GreedyRegisterAllocatorTests`, all isolating — verified to FAIL with the relevant R2 piece reverted)

- `ConstrainedValueLiveAcrossPrecolouredPhysClobber_SplitsNotSpills` — the
  split-not-spill rung (#1). Fails (spills) with the rung disabled.
- `RelocationSplitProduct_PrefersRealGprOverZeroPage` — the GPR preference (#3),
  with a no-GPR-hint across-clobber use so only the preference can steer it. Fails
  (lands in zp) with the preference disabled.
- `PinnedValueEvictsReassignableHeavierIncumbent_ByCost` — eviction-by-cost (#4),
  with a HEAVIER reassignable incumbent (so the default weight gate refuses) that
  the pinned value cannot split or rematerialize past. Fails (spills) with the
  by-cost clause disabled.

### Validation

- `dotnet test --project src/Irie.Tests` — **200/200 green** (197 + 3 new) with
  flips reverted.
- `dotnet test --project src/DotNetro.Compiler.Tests` — **21/21 green**.
- Convergence-check flips **all reverted** (`grep -rn 'CONVERGENCE-CHECK\|//CC\|PROBE'`
  returns nothing); the only committed diff is `GreedyRegisterAllocator.cs`,
  `SplitEditor.cs`, `RegisterAllocatorPass.cs`, and the unit-test file. No
  golden / `.irie` / `.s` / `.cs` lit file is touched — the default colourer path
  is byte-for-byte unaffected. Greedy stays flag-gated (`useGreedy: false`).

### Scope notes / boundary decisions

- **Stopped at the five corpus cases.** No EdgeBundles / SpillPlacement region
  split, no InterferenceCache, no last-chance recoloring, no wiring of memory-spill
  to the asm path (all out of scope per the plan).
- **Eviction-by-cost is defensive, not corpus-exercised** (see #4) — flagged
  rather than silently shipped, and isolated by a dedicated test so it is not dead
  code.
- **The LSB-first return-copy order is the structural cause** of the Mode-A
  residual spill; R2 resolves it by relocating the across-clobber value (matching
  what the colourer's funnel does eagerly), NOT by reordering ABI return copies —
  reordering would change the default golden path and is out of R2 scope.
