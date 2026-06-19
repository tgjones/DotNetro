# Constant canonicalization plan (Option 1)

Status: **complete** — implemented 2026-06-19 (commits "Fold constant add/sub
operands into adc.imm/sbc.imm in isel" + "Add generic-MIR verifier rejecting
inline value immediates"). Three plan assumptions proved inaccurate during
execution and were corrected:

1. **Part A — the `SbcImm` emit-table rule was missing**, not present as the
   plan claimed (only `AdcImm` existed). Added it.
2. **Part A — the fold defeated `MOS6502IncrementStrengthReductionPass`**, which
   matched `adc.zp` against a constant-1 zero-page slot. Once the constant folds
   to `adc.imm #$01` that match failed and `a++`/`b--` stopped reducing to
   INC/DEC. Fixed by teaching the pass to also match the post-fold `adc.imm` /
   `sbc.imm #$01` form (cleaner — the unit is inline, no `pseudo.copy 1` chase).
3. **Part B — the verifier must be scoped by dialect prefix, not pipeline
   position.** The plan assumed target-dialect MIR only ever enters mid-pipeline
   (via `--start-at`) and so is skipped by start-at gating. In fact many
   hand-written target-dialect `.irie` lit inputs (e.g. `WriteLineString-
   MachineCode`, `*-Binary`) run the *full* pipeline with no `--start-at`, so
   they hit pass #1. The invariant is inherently generic-dialect-only, so the
   verifier ignores any instruction whose dialect is not one of the seven
   MirBootstrap-registered generic dialects. Also added `mem.frame_addr` (use 0,
   slot index) to the allowlist — a structural attribute the plan's list omitted.

## Why

Irie represents an integer *value constant* two different ways today, and the
split is **incompleteness, not a deliberate dual design**:

- The front-end (`IlToMirTranslator`) and hand-written generic MIR are supposed
  to spell every value constant as an `arith.constant` def (`builder.BuildConstant`,
  [MirBuilder.cs:96](../src/Irie/Mir/MirBuilder.cs#L96)). The front-end already
  does this uniformly.
- Instruction selection is supposed to *fold* that constant into a target op's
  inline immediate operand field where the target has an immediate addressing
  mode (`adc.imm`, `cmp.imm`, …).

But the fold is implemented for **compare only** (`ResolveI8CmpRhs` +
`TryGetConstant`, [MOS6502InstructionSelector.cs:699](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L699)).
For `add`/`sub` there is no fold, which produces two symptoms:

1. **Missed optimization.** `mos6502.adc.imm` / `sbc.imm` exist and are fully
   wired (AMS refines `adc`→`adc.imm` when operand[3] is an `Immediate`,
   [MOS6502AddressingModeSelectorPass.cs:50](../src/Irie/Target/MOS6502/MOS6502AddressingModeSelectorPass.cs#L50);
   emit table maps them, [MOS6502MachineCodeEmitTable.cs:82](../src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs#L82))
   — but **unreachable for arithmetic**. `constraints/inc-dec` proves it: `a+1`
   emits `LDY #$01; STY $02; … ADC $02` instead of `ADC #$01`.
2. **Footgun.** The MIR parser accepts an inline literal in a generic op's value
   slot (`arith.addi %0, 1` parses), but the selector assumes a `VirtualReg` and
   `InvalidCast`-crashes on it (this is what originally blocked inc-dec).

This matches llvm-mos's model exactly: gMIR uses `G_CONSTANT` vreg **defs**;
literals only become inline operand fields at instruction selection (via `imm`
patterns). llvm-mos's generic IR does **not** permit inline literals in value
slots. Option 1 finishes that model in Irie.

### The invariant we are establishing

> In **generic-dialect** MIR, an integer *value* is never an inline `Immediate`
> operand — it is always an `arith.constant` def. Inline `Immediate`s in generic
> MIR are reserved for **structural attributes** (cmpi predicate, mem offsets,
> fill count, …) and for `arith.constant`'s own value. Instruction selection is
> the layer that folds a constant into a *target* op's inline immediate operand
> field (`adc.imm`, `sbc.imm`, `cmp.imm`). Post-isel target ops carry inline
> immediates freely — that is by design and unaffected by this work.

## Scope

In scope:
- **Part A** — complete the add/sub → `adc.imm`/`sbc.imm` constant fold in isel.
- **Part B** — a pre-pipeline verifier that rejects an inline `Immediate` in a
  generic value-operand slot with a clear error pointing at `arith.constant`.

Out of scope (note as follow-ups):
- Constant *folding* of arithmetic itself (`a+1-1 → a+b`); Irie still won't do
  this, so inc-dec stays longer than llvm-mos — but the `+1`/`-1` become single
  `adc #1`/`sbc #1` instead of register materializations.
- Immediate folding for ops other than add/sub/cmp (e.g. a hypothetical
  `and.imm`); not needed yet, and the verifier doesn't depend on it.

---

## Part A — add/sub immediate fold

### Where

All in [`MOS6502InstructionSelector.cs`](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs).
The single chokepoint is `SelectCarryBorrowOp` (line ~267), which is the common
lowering for **both** the bare i8 add/sub (`SelectBareAddSub`, via `SelectAddI`/
`SelectSubI`) **and** the per-byte carry/borrow chain links (`SelectAddCarry`/
`SelectSubBorrow`). Folding here covers i8, i16, i32 uniformly — the legalizer
narrows a wide `arith.constant` into per-byte constants
(`LegalizeNarrowConstant`, [LegalizerPass.cs](../src/Irie/Passes/LegalizerPass.cs)),
so each byte link sees a constant byte operand.

### What to add

In `SelectCarryBorrowOp`, *after* the existing absolute-load fold block (after
line ~285, before the `ReclassifyTo` calls) add an immediate-fold check that
mirrors the abs-load fold's structure:

```csharp
// Stage B': fold a constant operand into the ALU's immediate addressing mode,
// mirroring the cmp fold (ResolveI8CmpRhs) and llvm-mos's imm-operand patterns.
// adc is commutative so either input may become the immediate; sbc is not, so
// only the subtrahend (b) may fold.
if (TryGetConstant(function, bVreg) is { } bConst)
    return EmitFoldedImmCarryBorrow(
        builder, instr, targetOp, resultVreg, flagOutVreg,
        accVreg: aVreg, flagInVreg, immValue: bConst, foldedConstVreg: bVreg);
if (targetOp == MOS6502Op.Adc && TryGetConstant(function, aVreg) is { } aConst)
    return EmitFoldedImmCarryBorrow(
        builder, instr, targetOp, resultVreg, flagOutVreg,
        accVreg: bVreg, flagInVreg, immValue: aConst, foldedConstVreg: aVreg);
```

`TryGetConstant` ([line ~1015](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L1015))
already recognizes both `arith.constant` and the post-`SelectConstant`
`pseudo.copy <imm>` form, so the fold is robust to isel ordering (the constant
may or may not have been selected into a `pseudo.copy` yet).

Add `EmitFoldedImmCarryBorrow`, modeled on `EmitFoldedAbsCarryBorrow`
([line ~386](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs#L386)) but
(1) putting `new Immediate(immValue)` in the b slot instead of a `Symbol`, and
(2) doing the dead-constant cleanup instead of erasing an `lda.abs`:

```csharp
private static bool EmitFoldedImmCarryBorrow(
    MirBuilder builder, MirInstruction instr, MOS6502Op aluOp,
    int resultVreg, int flagOutVreg, int accVreg, int flagInVreg,
    long immValue, int foldedConstVreg)
{
    var function = builder.Function;

    // Accumulator stays broad (tied to def, like the plain path); carry-in
    // lives in the carry register.
    ReclassifyTo(function, accVreg,    MOS6502RegisterClass.Anyi8);
    ReclassifyTo(function, flagInVreg, MOS6502RegisterClass.Cc);

    var newResult = function.CreateVirtualRegisterInClass(
        MOS6502RegisterClass.Ac, MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
    var newFlag   = function.CreateVirtualRegisterInClass(
        MOS6502RegisterClass.Cc, MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

    builder.SetInsertionPointBefore(instr);
    builder.BuildInstruction(
        MOS6502Dialect.OpRef(aluOp),                 // stays pre-AMS adc/sbc; AMS → .imm
        new VirtualReg(newResult,  IsDefinition: true),
        new VirtualReg(newFlag,    IsDefinition: true),
        new VirtualReg(accVreg,    IsDefinition: false),
        new Immediate(immValue),                     // operand[3] → AMS picks AdcImm/SbcImm
        new VirtualReg(flagInVreg, IsDefinition: false));

    // Same out-funnel (Ac result → Anyi8) as the plain/abs paths.
    var resOut = function.CreateVirtualRegisterInClass(
        MOS6502RegisterClass.Anyi8, MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
    builder.BuildInstruction(
        PseudoDialect.OpRef(PseudoOp.Copy),
        new VirtualReg(resOut,    IsDefinition: true),
        new VirtualReg(newResult, IsDefinition: false));

    function.ReplaceAllUsesOfRegister(resultVreg,  resOut);
    function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
    builder.Remove(instr);

    // Dead-constant cleanup — mirror the cmp path (selector lines ~589-598).
    // The constant may feed several ops; only erase when its last use is gone.
    if (function.GetUseCount(foldedConstVreg) == 0
        && function.GetDefinition(foldedConstVreg) is { Parent: not null } def)
        builder.Remove(def);

    return true;
}
```

Notes:
- **No AMS or emit-table change.** AMS already refines `adc`/`sbc` to `.imm` when
  operand[3] is an `Immediate`; the emit table already encodes `AdcImm`/`SbcImm`.
  Verified in this exploration.
- **Commutativity / accumulator choice.** For `adc`, when `a` is the constant we
  fold it and make `b` the accumulator (the abs path does the identical swap).
  For `sbc` only `b` (the subtrahend) folds — never swap, since subtraction is
  non-commutative. The `targetOp == MOS6502Op.Adc` guard above enforces this.
- **Bare-add carry head still emitted.** `SelectBareAddSub` emits the `clc`/`sec`
  head before calling `SelectCarryBorrowOp`; that is unchanged. `a+1` becomes
  `clc; adc #$01` (the `clc` is required — there is no INC on a value already in
  $a unless RA happens to place it where INC applies; matching the bare path is
  correct and consistent with `add-i8`).
- **Edge case — both operands constant.** `arith.constant + arith.constant`
  should not normally survive to isel (the front-end doesn't emit it; a
  fold-constants pass would handle it). If it does, the `bVreg` branch fires
  first and folds b; `a` stays a materialized constant in the accumulator. Still
  correct, just not maximally folded. Acceptable; note as a non-goal.

### Expected codegen change (inc-dec)

`a++` → `adc #$01`, `b--` → `sbc #$01`; the shared `arith.constant 1` is folded
into both and cleaned up. Estimate ~7-9 instructions (down from 13). Regenerate
the golden (see Part C) — do not hand-compute it.

---

## Part B — verifier: reject inline value literals in generic MIR

### Mechanism

Add a virtual to the `Dialect` base ([Dialect.cs](../src/Irie/Mir/Dialect.cs),
next to `TryFormatImmediateUse`):

```csharp
// True if an Immediate at this use-operand index is a legal structural
// attribute (cmpi predicate, mem offset/count, …) or arith.constant's value —
// as opposed to an illegal inline value literal that must be an arith.constant
// def. `useIndex` is 0-based among USE operands (defs excluded), matching the
// TryFormatImmediateUse convention. Default: no legal immediate operands.
public virtual bool IsLegalImmediateOperand(ushort code, int useIndex) => false;
```

Overrides (allowlist — derived from each dialect's operand-layout comments):

- **`ArithDialect`** ([ArithDialect.cs](../src/Irie/Dialects/Arith/ArithDialect.cs)):
  - `Constant`, useIndex 0 — the canonical constant value.
  - `CmpI`, useIndex 0 — the predicate (already special in `TryFormatImmediateUse`).
- **`MemDialect`** ([MemOp.cs](../src/Irie/Dialects/Mem/MemOp.cs) layouts):
  - `Fill`, useIndex 2 — the count. **(Required: `initobj` emits this in input MIR.)**
  - `LoadByteAt`, useIndex 1 — the offset.
  - `StoreByteAt`, useIndex 1 — the offset.
- **`PseudoDialect`** (future-proofing; these only appear post-legalizer/RA so a
  front-only verifier won't see them, but declaring them is cheap and correct):
  - `Extract`, useIndex 1 — bit offset.
  - `Insert`, useIndex 2 — bit offset.
  - `Spill`, useIndex 0 — slot index (`[Immediate, reg-use]`, no def).
  - `Reload`, useIndex 0 — slot index (`[reg-def, Immediate]`).

`Cf`, `Core`, `Cast`, `Call` have **no** legal inline immediates (their operands
are vregs / `Symbol` / `BlockTarget`), so they keep the `false` default.

> Strictly, a front-only verifier (see below) only needs `arith.constant`,
> `arith.cmpi`, and `mem.fill`, since those are the only immediate-bearing ops
> present in input MIR. The rest are declared for robustness if the verifier is
> ever reused later in the pipeline.

### The verifier

Add `src/Irie/Passes/GenericMirVerifierPass.cs` — a `MirFunctionPass` (LLVM's
`VerifierPass` precedent), **not** a static check. It mutates nothing; it throws
`MirVerificationException` on the first violation.

```csharp
public sealed class GenericMirVerifierPass : MirFunctionPass
{
    // Verifies generic-MIR invariants. Currently: no inline Immediate in a
    // generic op's value-operand slot (use arith.constant). Only valid on
    // generic (pre-isel) MIR — see "Why a pass" below.
    public override void Run(MirFunction function) { /* iterate blocks/instrs */ }
}
```

For each instruction, resolve its `Dialect` (via `DialectRegistry.ById`), compute
`defCount` = number of leading operands that are `VirtualReg`/`PhysicalReg` with
`IsDefinition == true`, then for each `Immediate` operand at array index `i`:
`useIndex = i - defCount`; if `!dialect.IsLegalImmediateOperand(code, useIndex)`,
throw with a message like:

```
MirVerifier: arith.addi (%3) has an inline immediate value operand (1) at use 1.
Value constants must be an arith.constant def: `%c = arith.constant 1` then
`arith.addi %x, %c`. Inline immediates are only allowed for structural
attributes (e.g. the cmpi predicate) and arith.constant's own value.
```

(Include the function name + op name + offending value in the real message.)

### Pipeline placement

Add as the **first** pass in both pipelines:

- `iriec` — [Program.cs](../src/Irie.Tools.Compiler/Program.cs#L71) as the first
  `passMgr.AddPass(...)`, before `ReturnMergePass`.
- `CompilerDriver` — [CompilerDriver.cs](../src/DotNetro.Compiler/CompilerDriver.cs#L27)
  likewise, before `ReturnMergePass`.

### Why a pass (not an always-run static check)

The verifier enforces a **generic-MIR** invariant. That invariant is deliberately
*false* mid-pipeline — post-isel, `adc.imm #$01` legitimately carries an inline
immediate. So the verifier must run **only on generic (pre-isel) MIR**. Pipeline
position gives exactly that:

- A full compile runs it first, always (the entry MIR is generic). `CompilerDriver`
  constructs its `PassManager(null, null)`, so the IL→MIR end-to-end path always
  runs it too.
- A mid-pipeline single-pass run (`--start-at LegalizerPass`, `--run-pass
  InstructionSelectorPass`, …) **correctly skips** it via the `PassManager`'s
  existing start-at gating — because the input there is non-generic and must not
  be checked against the generic invariant. This is the planned ".irie lit tests
  that feed mid-pipeline MIR and run a single pass" use case; a static check that
  always ran would actively *break* it by rejecting valid post-isel immediates.

The only way to skip the verifier is to explicitly start mid-pipeline, which is
the deliberate "I'm testing one pass" case — never an accidental skip on a normal
compile. Because it is pass #1, the allowlist only needs the immediate-bearing ops
reachable in input MIR (`arith.constant`, `arith.cmpi`, `mem.fill`); the other
allowlist entries are harmless future-proofing. (`--run-pass GenericMirVerifierPass`
also makes it runnable in isolation for its own tests.)

---

## Part C — test & golden impact

The migration set for the verifier is **empty**: a grep confirmed no existing
`.irie` test uses an inline literal in an arith/mem value slot, and **0** `.cs`
lit tests pin `--emit=asm`. (`constraints/inc-dec.irie` already uses
`arith.constant`.) So Part B should not require editing any test input.

The fold (Part A) changes emitted asm for **every** case that adds/subtracts a
literal constant. Goldens that will move (regenerate, don't hand-edit):

- `constraints/inc-dec` — the headline case.
- Any `LlvmMosReference` case with a literal add/sub: check `loop-counter`
  (`i++`), `early-return`, `if-else`, `fibonacci`, `select`, and any others.
- Any other `Lit/CodeGen/**` `.irie` golden whose body adds/subtracts a constant.

Regeneration: each golden's `CHECK` block is the complete `--emit=asm` listing,
regex-escaped (`. $ ( ) # +` → `\.` `\$` `\(` `\)` `\#` `\+`), per CLAUDE.md
house style. **Generate goldens the way the harness runs them, or verify under
`dotnet test` afterward** — not from a one-off standalone `iriec` run (CLAUDE.md
warns of latent frame-slot nondeterminism; inc-dec is not affected, but follow
the rule for any case that is).

Also update the prose that this change makes stale:
- `constraints/inc-dec.irie` header comment — it currently claims `arith.addi %x, 1`
  "is not selectable" and that Irie "folds nothing". After this change: an inline
  immediate is a **verifier error** (not "not selectable"), and the constant **is**
  folded into `adc.imm`/`sbc.imm` (the `+1`/`-1` become single immediate ALU ops,
  though `a+1-1 → a+b` arithmetic folding still does not happen).
- The `inc-dec` caveat in
  [`LlvmMosReference/README.md`](../src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/README.md)
  — reword "Irie folds nothing, so the +1/-1 survive as real byte ops" to reflect
  that they now fold into immediate ALU ops (the closed-form `a+b` fold is what
  Irie still lacks).

## Validation

```bash
dotnet build src/Irie.Tools.Compiler -c Debug
# Spot-check the headline case:
dotnet src/Irie.Tools.Compiler/bin/Debug/net10.0/iriec.dll \
    --target mos6502 --emit=asm \
    src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/constraints/inc-dec.irie
# Negative test for the verifier (should error cleanly, not InvalidCast):
#   write a scratch .irie with `arith.addi %0, 1` and confirm the MirVerifier message.

dotnet test --project src/Irie.Tests                 # all MIR/asm lit + unit tests
dotnet test --project src/DotNetro.Compiler.Tests    # end-to-end (emulator) tests

# Refresh the scoreboard and confirm the ratio moves the right way:
dotnet run --project src/Irie.Tools.Reference
#   expect inc-dec's Irie count to drop (and aggregate ratio to improve).
```

Also add targeted unit/lit coverage:
- An `.irie` lit test (or extend an existing one) asserting `adc.imm`/`sbc.imm`
  for a literal add/sub — locks in the fold so it can't silently regress.
- A `GenericMirVerifierPass` unit test (Irie.Tests) for the rejection path:
  running the pass over a function with an inline value immediate in `arith.addi`
  throws `MirVerificationException`; a `cmpi` predicate, a `mem.fill` count, and an
  `arith.constant` value all pass cleanly.

## Risks & edge cases

- **Pre-existing `IntegerAdd32-Ssd` failure.** This lit test fails on a clean
  tree today (unrelated SSD byte-layout golden). Don't be alarmed by it; confirm
  the failure set is unchanged (only that one) after the work.
- **Dead-constant cleanup correctness.** A constant shared by a folded *and* an
  unfolded use (e.g. one add folds, one cmpi takes the `slt` non-folding path)
  must survive — the `GetUseCount == 0` guard handles this (same as the cmp path).
- **Allowlist completeness.** If the verifier throws on a *currently passing*
  test, that's a missing allowlist entry, not a bad test — grep that dialect's
  op-layout comments for `Immediate` and add the use index. The full suite is the
  safety net.
- **Selection order of the constant.** Handled: `TryGetConstant` matches both the
  `arith.constant` and `pseudo.copy <imm>` forms, so it doesn't matter whether
  the constant was selected before or after the add/sub.
```
