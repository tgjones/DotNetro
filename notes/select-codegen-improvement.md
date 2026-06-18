# Codegen improvement: `control-flow/select` (worst llvm-mos-reference case)

Follows the improvement workflow in
[`ext/llvm-mos-reference/README.md`](../ext/llvm-mos-reference/README.md).

## Target

`irie-report` baseline (rebuilt 2026-06-18):

- Aggregate: **Irie 338 vs llvm-mos 301 instructions (+12.3%)**, 15/46 paired.
- Worst ratio: **`control-flow/select`** at **21 vs 12 (1.75×)**.

```c
int select_val(int cond, int a, int b) { return cond ? a : b; }
```

## How to reproduce the measurements

```bash
# headline + per-case scoreboard (writes ext/llvm-mos-reference/report.html)
dotnet run --project src/Irie.Tools.Reference

# Irie's pass-by-pass MIR for this case (stderr); --emit=asm keeps final asm on stdout
IRIEC=src/Irie.Tools.Compiler/bin/Release/net10.0/iriec
$IRIEC --target mos6502 --emit=asm --print-changed \
    src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/control-flow/select.irie 2>&1 >/dev/null

# llvm-mos's final asm and pass-by-pass dump for the same case
cat ext/llvm-mos-reference/control-flow/select.{s,txt}
```

## Root-cause analysis

The 9 extra instructions decompose into three independent gaps, confirmed by
reading Irie's `--print-changed` dump beside llvm-mos's `.txt`.

### Gap 1 — Irie materializes the constant `0` to compare against it (≈4 instrs)

The `Legalizer` narrows `arith.cmpi ne %cond:i16, 0` into a per-byte
lexicographic tree whose leaves compare each byte against an `arith.constant 0`
*vreg*. `MOS6502InstructionSelector` then funnels that constant through `Anyi8`
and emits a **register** `mos6502.cmp`, so the zero gets materialized into zero
page:

```
LDY #$00 / STY $07 / LDY #$00 / STY $06   ; materialize two zero bytes
... CMP $06 ... CMP $07                    ; compare against them
```

llvm-mos never materializes a zero — its `CmpZero` / `CmpBrZero` compare against
**immediate 0**. This is the same class of missing fold Irie already has for ALU
ops (`adc.abs` / `sbc.abs` fold a memory operand); it is simply absent for `cmp`.

### Gap 2 — no "flag-from-load/transfer" fold (≈2 instrs)

llvm-mos's `mos-late-opt` pass replaces `CmpZero $r` + branch with the N/Z
side-effect of an already-present load or transfer (`tax`,
`LDImag8 … implicit-def $nz`), so the compare-against-zero costs *zero* extra
instructions. Irie models flags only via explicit `mos6502.cmp` and does not
know that loads/transfers set N/Z.

### Gap 3 — no fall-through / empty-block elimination (≈2-3 instrs)

Irie emits an explicit `JMP` on every edge and keeps an empty `bb1` that only
does `JMP .bb3`. llvm-mos's `branch-folder` (Control Flow Optimizer) +
`block-placement` drop both via fall-through.

Note: llvm-mos also coalesces the phi result into `b`'s registers; Irie already
does the symmetric thing — coalesces into `a`'s regs — so the phi handling
itself is **not** a gap.

## Working agreement on pass placement

- **Do not change the pipeline order without asking first.** Inserting,
  removing, or reordering passes in `iriec`'s pipeline is a design decision that
  must be confirmed with the maintainer before implementing.
- **Insert passes where llvm-mos puts them, unless we have a good reason not
  to.** llvm-mos is the reference (per CLAUDE.md). If llvm-mos has a
  `mos-late-opt`, we should have a MOS6502-specific late-optimization pass too,
  positioned analogously. The same applies to the branch-folder /
  block-placement work in Stage 3.

For reference, the relevant llvm-mos late-pipeline order (from
`select.txt`) is:

```
Greedy Register Allocator → Virtual Register Rewriter
  → Control Flow Optimizer (branch-folder)        # runs *before* pseudo expansion
  → Machine Copy Propagation / Optimize copies for MOS (mos-copy-opt)
  → Post-RA pseudo instruction expansion (postrapseudos)
  → Finalize ISel and expand pseudo-instructions (finalize-isel)
  → Post-RA pseudo instruction expansion (postrapseudos)
  → MOS Late Optimizations (mos-late-opt)          # the flag-from-load fold
  → Branch Probability Basic Block Placement (block-placement)
  → Branch relaxation
```

Irie's current order (from `--print-changed`):

```
FrameLowering → AbiLowering → Legalizer → MOS6502SelectLowering
  → InstructionSelector → PhiElimination → TwoAddressInstruction
  → RegisterAllocator → CopyElimination → MOS6502AddressingModeSelector
  → MOS6502IncrementStrengthReduction → MOS6502ParallelCopy
  → FrameAccessLowering → PrologueEpilogueInsertion → PseudoExpansion
  → RegisterScavenging
```

## Stage 1 — fold immediate compare operands (no pipeline change)

Teach the compare selector to pass a constant `b`-operand as an `Immediate` to
`mos6502.cmp` instead of funnelling it through a register. The existing
`MOS6502AddressingModeSelectorPass` already refines a `mos6502.cmp` with an
immediate slot-1 operand to `mos6502.cmp.imm`, and `MOS6502MachineCodeEmitTable`
already maps `CmpImm` — so **no new ops and no pipeline change**; the work is
confined to `MOS6502InstructionSelector.cs`.

Edits (all in
[`src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs`](../src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs)):

1. Add a helper `TryGetConstant(function, vreg) -> long?` (def is
   `arith.constant` / immediate).
2. In `EmitMultiByteCmpLadder`: for each byte where `bByte` is a constant, skip
   `FunnelThroughAnyi8` / `ReclassifyTo(Imag8)` and emit a `mos6502.cmp` whose
   rhs is an `Immediate`. Restructure so only non-constant `b` bytes are
   funnelled. Limit to the `eq` / `uge` chains (the zero-compare path); leave the
   `slt` signed-flip path unchanged to avoid scope creep.
3. Generalize `EmitCmp` to accept a `MirOperand` rhs (register or immediate).
4. In `EmitI8CmpBranch`, extend the `Eq` / `Uge` arms to emit an immediate rhs
   when `bOperand` is a constant (today only `Slt`-vs-0 uses an immediate).
5. Collect the now-dead `arith.constant` defs into the `toRemove` list in
   `SelectCondBr` so they are erased rather than selected into stray
   `pseudo.copy`s.

**Expected result:** `select` drops 21 → **17** (removes the 4
zero-materialization instrs; the two `CMP $0x` become `CMP #$00`), ratio
1.75 → **1.42**. Generalizes to every compare-against-constant case (also helps
`early-return`, `compare-flags`, …), so the headline aggregate moves too.

**Verify, then commit:**
- `iriec --emit=asm` on the case; regenerate the golden `CHECK` block in
  [`select.irie`](../src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/control-flow/select.irie).
- `dotnet test --project src/Irie.Tests` (full suite — lit tests can't be
  filtered individually; catches regressions in the other compare cases).
- `dotnet run --project src/Irie.Tools.Reference`; confirm `select`'s ratio and
  the headline aggregate both drop.
- **Commit** ("fold immediate operand into cmp; drop zero materialization").

## Stage 2 — flag-from-load fold (MOS6502 late-optimization pass)

Closes Gap 2 (~2 more instrs). This is the `mos-late-opt` analogue: a compare
against zero whose operand was just produced by a flag-setting op (a load or
register transfer) is redundant — the N/Z flags are already correct, so the
explicit `cmp #0` can be dropped and the branch reads the existing flags.

Approach:

1. Model that the relevant MOS6502 loads/transfers set N/Z — i.e. give
   `mos6502.lda.zp` / `.ldx.zp` / `.ldy.zp` / `tax` / `tay` / `txa` / `tya`
   (and friends) implicit `$n` / `$z` defs in `MOS6502InstructionInfo`.
2. Add a **new MOS6502-specific late-optimization pass** (e.g.
   `MOS6502LateOptimizationPass`) that pattern-matches `cmp.imm $r, #0` (or its
   pre-AMS `mos6502.cmp`) immediately preceded by a flag-setting def of `$r`
   with no intervening flag clobber, and erases the compare, rewiring the branch
   to the load/transfer's flags. Mirror llvm-mos's `MOSLateOptimization` /
   `MOSCombiner` handling for the zero-compare case.

**Pipeline placement — confirm with maintainer before implementing.** llvm-mos
runs `mos-late-opt` *after* post-RA pseudo expansion. The Irie analogue is to
run this pass after `PseudoExpansion` (so it sees the expanded
loads/transfers). Do not wire it in without sign-off on the position.

**Verify, then commit:** regenerate `select.irie`'s golden block; full
`Irie.Tests`; re-run the report. `select` should reach ~15. **Commit.**

## Stage 3 — fall-through + empty-block elimination (branch layout)

Closes Gap 3 (~2-3 instrs) and brings `select` to parity (12). Irie analogues of
llvm-mos's `branch-folder` (Control Flow Optimizer) and `block-placement`:

1. **Empty-block elimination**: a block whose only instruction is an
   unconditional `mos6502.jmp.abs` to a successor can be folded away, redirecting
   its predecessors (e.g. the empty `bb1 → bb3`).
2. **Fall-through**: when a block's unconditional terminator targets the block
   that is physically laid out next, drop the `JMP` and fall through. Requires a
   block-placement notion (order blocks so the most common successor follows).

**Pipeline placement — confirm with maintainer before implementing.** llvm-mos
runs branch-folder before pseudo expansion and block-placement late (after
`mos-late-opt`). Decide (with sign-off) whether Irie wants one combined pass or
two, and where each sits. Do not change the pipeline without asking.

**Verify, then commit:** regenerate `select.irie`'s golden block; full
`Irie.Tests`; re-run the report. Target: `select` 12 vs 12 (parity). **Commit.**

## Done criteria

- `select` ratio 1.75 → parity (1.0) across the three stages.
- No regressions in the full `Irie.Tests` suite.
- Headline aggregate improves at each stage (Stage 1 alone helps every
  compare-against-constant case).
