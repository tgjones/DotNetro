# Plan: close the isel class-intersection gap (move constraint copies into isel)

Implements the register-allocator redesign plan's **§3.2 "preferred path"**: the
instruction selector should give flexible values their natural broad class and
insert an explicit `pseudo.copy` into a single-physreg class *only at the operand
that truly requires it*, leaving the original value broad. The Phase-3 coalescer
then removes the copy whenever the pinned register is free. This retires the last
place where instruction selection over-constrains a value's class and makes RA
throw.

This plan is self-contained: it records the exact mechanism and the file:line
state at the time of writing, so execution needs no further exploration.

---

## 1. Symptom

RA aborts with:

```
RegisterAllocator: vreg %N in class ac has an empty allocatable set
after intersecting operand-class constraints.
```

(thrown at [GraphColouringAllocator.cs:355-358](../src/Irie/Passes/GraphColouringAllocator.cs#L355-L358)).

It blocks the corpus shape `a > b ? a - b : b - a` (and the `s += i` loop form):
the *same* byte of `a` is used as a **minuend** in one arm (`a - b`) and as a
**subtrahend** in the other (`b - a`). The Phase-4 corpus conversions worked
around it by choosing shapes with consistent operand roles
([RaCorpus/README.md](../src/Irie.Tests/Lit/CodeGen/MOS6502/RaCorpus/README.md)).

## 2. Root cause

The MOS6502 byte ALU ops declare **hard operand-class constraints**
([MOS6502Dialect.cs:299-336](../src/Irie/Target/MOS6502/MOS6502Dialect.cs#L299-L336)):

- `AdcInfo` / `SbcInfo`: `use[0]` (the L/minuend operand) = `Ac` (must be `$a`,
  and is *tied* to `def[0]`), `use[1]` (R operand) = `Imag8` (zero-page).
- `CmpInfo`: `use[0]` (a) = `Ac`, `use[1]` (b) = `Imag8`. **Not tied.**

`ComputeAllowedColours` ([GraphColouringAllocator.cs:213-361](../src/Irie/Passes/GraphColouringAllocator.cs#L213-L361))
computes each vreg's allocatable set as the **intersection** of its annotation's
class and the operand-class constraint of *every* site it fills. So a single SSA
value used at an `Ac` site and an `Imag8` site intersects `{$a}` with
`{RC2..RC31}` = ∅ → throw. The intersection is *correct*; the bug is upstream —
isel forces one value to be in two incompatible places with no copy to decouple
them.

Concretely, the selector pins **pre-existing** operand vregs in place via
`ReclassifyTo(...)` at two sites:

1. **`SelectCarryBorrowOp`** (adc/sbc), [MOS6502InstructionSelector.cs:206-208](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L206-L208):
   ```csharp
   ReclassifyTo(function, aVreg,      MOS6502RegisterClass.Ac);
   ReclassifyTo(function, bVreg,      MOS6502RegisterClass.Imag8);
   ReclassifyTo(function, flagInVreg, MOS6502RegisterClass.Cc);
   ```
   It then builds the `mos6502.adc`/`sbc` directly over `aVreg`/`bVreg`, and does
   `ReplaceAllUsesOfRegister(resultVreg, newResult)` where `newResult` is `Ac`
   ([:226](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L226)) — so
   the **result is pinned to `$a` for every downstream use too.**
2. **cmp + cond_br fusion**, [MOS6502InstructionSelector.cs:356-357](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L356-L357):
   ```csharp
   ReclassifyTo(function, aVreg, MOS6502RegisterClass.Ac);
   ReclassifyTo(function, bVreg, MOS6502RegisterClass.Imag8);
   ```
   then builds `mos6502.cmp` directly over `aVreg`/`bVreg`.

The intersection then sees both an `Ac` and an `Imag8` constraint on the shared
byte. (For adc/sbc, `use[0]` is tied so `TwoAddressInstructionPass` later moves
that use onto a fresh copy — but the **in-place annotation** and the **result
pinning** still over-constrain the value; for cmp, `use[0]` is untied so the pin
is direct.)

**The correct pattern already exists in this file** and is the template to
follow: the wider (i16/i32) compare path funnels each operand through a fresh
vreg — `FunnelThroughAnyi8` ([:566-575](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L566-L575))
plus a fresh `Ac` copy ([:498-517](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L498-L517)) —
and the EOR path funnels its result back **out** to `Anyi8`
([:530-558](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L530-L558)).
The adc/sbc and simple-cmp paths simply never adopted it.

## 3. The fix (one principle, two sites)

**Principle:** never `ReclassifyTo` a *pre-existing* operand vreg into a
single-physreg class in place, and never replace a value's downstream uses with a
single-physreg-classed def. Instead:

- route each operand that an instruction pins to a fixed register **through a
  fresh vreg of that class** via `pseudo.copy`, leaving the original value broad;
- funnel a fixed-register **result out** to a fresh broad (`Anyi8`) vreg before
  redirecting downstream uses.

The coalescer (Phase 3) collapses every one of these copies whenever the pinned
register is free, so straight-line chains keep their current tight output; the
copies only survive (as real moves) exactly when the value genuinely needs to be
in two places — which is correct.

### 3.1 `SelectCarryBorrowOp` (adc / sbc) — [MOS6502InstructionSelector.cs:194-230](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L194-L230)

- **Remove** `ReclassifyTo(aVreg, Ac)` and `ReclassifyTo(bVreg, Imag8)`. Keep
  `ReclassifyTo(flagInVreg, Cc)` (a flag value genuinely must live in the carry
  register and has no multi-role conflict).
  - `use[0]` (a) is **tied** to `def[0]`, so `TwoAddressInstructionPass` already
    materialises it as a fresh copy that picks up the `Ac` operand constraint —
    dropping the in-place annotation is sufficient for `a`. `aVreg` stays broad.
  - `use[1]` (b) keeps the `Imag8` operand constraint from `AdcInfo` directly;
    `bVreg` left broad intersects to `Imag8` (zero-page), which is correct. No
    funnel copy needed for `b` because, with the in-place `Ac` pin gone, **no
    value vreg can ever carry an `Ac` operand constraint** (the only `Ac` use is
    `use[0]`, always tied→copied), so `Ac ∩ Imag8` can no longer arise on a
    shared byte.
- **Funnel the result out.** Keep creating `newResult` in `Ac` (the adc/sbc
  architecturally produces its result in `$a`), but instead of
  `ReplaceAllUsesOfRegister(resultVreg, newResult)`, emit
  `%resOut : any8 = pseudo.copy %newResult` *after* the adc and
  `ReplaceAllUsesOfRegister(resultVreg, resOut)`. This makes the result's
  annotation `Anyi8` (flexible) rather than `Ac`, so a result later used as a
  `b`/zero-page operand no longer intersects `Ac ∩ Imag8` → ∅. Use a
  `MirBuilder` insertion point after the new adc instruction (mirror the EOR
  path's out-funnel at [:553-558](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L553-L558)).

> Note on the tied result + out-funnel: `def[0]` (newResult, `Ac`) and `use[0]`
> (the TwoAddress copy, `Ac`) coalesce to `$a`; `resOut` (`Anyi8`) coalesces onto
> `$a` too when `$a` is free, so the straight chain collapses to today's output.
> When the result must also live in zero page, `resOut` stays as a real
> `sta.zp`/transfer — correct.

### 3.2 cmp + cond_br fusion — [MOS6502InstructionSelector.cs:356-368](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L356-L368)

`cmp`'s `use[0]` is **not** tied, so the in-place `Ac` pin is direct and must be
replaced by an explicit funnel (exactly like the wider-compare path):

- **Remove** `ReclassifyTo(aVreg, Ac)` and `ReclassifyTo(bVreg, Imag8)`.
- Create `%aCmp : ac = pseudo.copy %aVreg` immediately before the `cmp`, and use
  `%aCmp` (not `aVreg`) as `cmp`'s `use[0]`. Leave `aVreg` broad.
- Leave `bVreg` as `cmp`'s `use[1]`; the `Imag8` operand constraint applies to it
  directly and, with no `Ac` pin reaching `bVreg`, cannot conflict. (Funnelling
  `b` as well is harmless and more uniform, but is not required to close the gap —
  prefer the minimal change unless the wider-compare path's symmetry is wanted.)

Model this on lines [498-517](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L498-L517),
which already do precisely this for the multi-byte compare.

### 3.3 Audit for any third site

Grep the selector for `ReclassifyTo(function, <existingOperandVreg>, <single-physreg class>)`
and confirm only these two sites pin a *pre-existing* operand vreg (as opposed to
a *freshly created* one, which is fine). The wider-compare/EOR paths already
funnel and must be left as-is. Document the audit result in the PR.

## 4. Why this is safe now (it wasn't pre-Phase-3)

Before the coalescer existed, inserting these copies would have left copy bloat
that nothing cleaned up — which is why §8 of the redesign plan flagged the
preferred path as "potentially invasive" and the interim path (intersection in
RA) was taken. With Phase 3's iterated register coalescing + the
`CopyEliminationPass` safety net, copy-related vregs that can share a register do,
and the inserted copies vanish in the common case. This is the same mechanism
that already collapses the EOR/compare funnels. The intersection logic in
`ComputeAllowedColours` is kept unchanged — it remains correct and is the backstop.

## 5. Validation

- **New RaCorpus test:** add `IfElseAbsDiff-RegisterAllocator.irie` (or similar)
  encoding the previously-blocked `a > b ? a - b : b - a` shape (use supported
  predicates per the cmpi coverage — eq/ne/ult/uge/slt/sge), `; RUN: @iriec
  --target mos6502 --stop-after RegisterAllocator @file`, CHECK-pinning the now-
  successful allocation. Update `RaCorpus/README.md` to mark it converted and
  remove the "isel class-intersection gap" caveat.
- **No copy-count regression:** for `AddI16`/`AddI32`/`SubI16`/`IntegerAdd32`/
  `IntegerSub32` and the existing if-else/loop/fibonacci cases, confirm the
  post-coalescing copy counts are **unchanged** (the funnels must coalesce away).
  If any case gains a surviving copy, investigate before regenerating its CHECK
  block — a surviving funnel copy is the failure mode to watch for.
- **Regenerate characterization CHECK blocks** mechanically for any case whose
  output legitimately shifts (run `iriec --stop-after RegisterAllocator` / the
  full pipeline), reviewing each diff is neutral or an improvement.
- **Both suites green:** `dotnet test --project src/Irie.Tests` (baseline 161) and
  `dotnet test --project src/DotNetro.Compiler.Tests` (32/33 — only the pre-existing,
  unrelated `Lit/Mir/ForLoop` `dialect=3,code=0` IL→MIR failure may remain). The
  emulator-executed correctness tests must stay green (the result out-funnel and
  cmp funnel change register choices; correctness must not move).
- Determinism: output must be byte-stable across repeated runs (the coalescer is
  deterministic; new copies must not introduce nondeterministic ordering).

## 6. Risks / open questions

- **Result out-funnel + tied operand interaction.** The adc result is `def[0]`,
  tied to `use[0]`; the out-funnel reads `def[0]`. Confirm the funnel `pseudo.copy`
  is inserted *after* the adc (so it reads the post-write value) and that
  TwoAddress/coalescing still collapse the chain. The EOR path is the working
  precedent.
- **Incoming operand annotation.** After removing the in-place reclassify,
  `aVreg`/`bVreg` rely on their pre-isel annotation (a legalized `TypedVReg(i8)`
  or `Anyi8`). `ComputeAllowedColours` maps both to the flexible class
  ([GraphColouringAllocator.cs:292-296](../src/Irie/Passes/GraphColouringAllocator.cs#L292-L296)).
  Verify no operand byte arrives with a wider or unclassable annotation; if one
  does, that is a separate legalizer gap to surface, not to paper over.
- **Scope creep into the legalizer.** §8 warns the preferred path "could ripple
  into the legalizer's class assignments." This plan touches only the selector and
  adds tests; if a legalizer change proves necessary, stop and reassess rather
  than widening scope silently.

## 7. Out of scope

- Removing the `touchedByNonCopy` / intersection machinery in
  `ComputeAllowedColours`. §3.2 says the preferred path "would make the declared
  OperandClasses complete and retire this scan," but that cleanup is a separate,
  larger change; the intersection stays as the correct backstop here.
- The unrelated `Lit/Mir/ForLoop` IL→MIR `dialect=3,code=0` selection gap.
