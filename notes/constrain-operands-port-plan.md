# Plan: port llvm `constrainSelectedInstRegOperands` to Irie isel

Self-contained continuation plan. Phase 1 is **landed and committed**; Phases 2–3
remain. Read this top-to-bottom; it should not require re-exploration to execute.

## Why

Irie's MOS6502 instruction selector used to keep ALU operands in the broad
`Anyi8` class and lean on the register allocator's per-vreg class-intersection
(`GraphColouringAllocator.ComputeAllowedColours`) to pin them to `$a`/zero-page.
That was a *workaround* for the empty-intersection hazard: if one vreg were pinned
`Ac` at one operand and `Imag8` (zero page) at another, `Ac ∩ Imag8 = ∅` and RA
throws ("empty allocatable set").

llvm-mos does **not** do this. It narrows each selected op's operand to that
operand's register class and inserts a split `COPY` only when one vreg is genuinely
needed in two disjoint classes. We are porting that mechanism so Irie's MIR matches
llvm's structure (e.g. an `adc` tied use is `ac`, not `any8`) and so future cases
get llvm's split-on-conflict behaviour instead of the bespoke dodge.

### Verified llvm mechanism (don't re-derive)

The split is **not** use-scanning inside one call — it is stateful per-vreg class
narrowing across operand-processing order:

- `constrainSelectedInstRegOperands` (llvm `CodeGen/GlobalISel/Utils.cpp:155`)
  loops every reg operand of a just-selected instr and calls
  `constrainOperandRegClass` (`Utils.cpp:56`).
- `constrainGenericRegister` (`CodeGen/RegisterBankInfo.cpp:131`): if the vreg has
  only a *bank* → `setRegClass(req)` directly (no copy, line 145); if it already
  has a *class* → `MachineRegisterInfo::constrainRegClass` = `getCommonSubClass`
  (`MachineRegisterInfo.cpp:68`), which returns null on empty intersection.
- Null → `constrainRegToClass` mints a fresh vreg in `req` (`Utils.cpp:50-51`) and
  `constrainOperandRegClass` inserts a `COPY` (`Utils.cpp:73-87`, use→before,
  def→after).

Empirically confirmed this session: compiling a byte that feeds both an `adc`
accumulator (`ac`) and a zero-page operand (`imag8`) produced `%12:ac = COPY %1`
at `InstructionSelect`, absent at `RegBankSelect`. (C repro: a value used as an
`adc` accumulator in one place and stored to globals / used as a zp operand
elsewhere — see `/tmp/twouse.c` shape if regenerating.)

Irie's class lattice is **nested** (`Anyi8 ⊃ Axy ⊃ {Ac,Xc,Yc}`, `Anyi8 ⊃ Imag8`),
so a subset test is a sufficient `getCommonSubClass`.

## Phase 1 — DONE (commit on main)

Instruction-count-neutral on the 46-case corpus (aggregate −7.8% unchanged, every
paired case identical before/after — verified by stash + `irie-report`). add-i8
asm byte-identical. 226/226 solution tests pass; 21/21 emulator-execution tests.

- `TargetRegisterInfo.TryGetCommonSubClass(a,b)` — `getCommonSubClass` analogue;
  default impl assumes nested classes (returns the subset class, null if disjoint,
  throws on a non-nested partial overlap).
- `src/Irie/Target/RegisterClassConstraining.cs` (new) — the universal utility,
  `ConstrainSelectedInstRegOperands(instr, function, registerInfo, builder)`.
  TypedVReg (bank-only) → set class directly; ClassedVReg → narrow-in-place or
  split with `pseudo.copy`. Keying on Typed-vs-Classed avoids a flag-copy hazard
  for i1 carries (a typed i1 carry must NOT be routed through an 8-bit flexible
  class; setting `Cc` directly is correct).
- `MOS6502InstructionSelector`: the adc/sbc chain became instance methods (needs
  `_registerInfo`); removed the `ReclassifyTo(aVreg, Anyi8)` /
  `ReclassifyTo(flagInVreg, Cc)` dodges; calls the utility after building the op at
  the 3 sites — `SelectCarryBorrowOp` (plain), `EmitFoldedAbsCarryBorrow`,
  `EmitFoldedImmCarryBorrow`. Ctor now takes `TargetRegisterInfo`; wired in
  `MOS6502Target` (`new MOS6502InstructionSelector(new MOS6502RegisterInfo())`).
- Result: adc tied use `ac` (was `any8`), R operand `zp`. 7 lit goldens
  regenerated (4 MIR-label, 3 ASM-reallocation) — see "Regenerating goldens".

**Unchanged on purpose:** `TwoAddressInstructionPass` (reads the now-`ac` tied use
and types the tie copy `ac` — matches llvm); the coalescer (already coalesces
across classes via allowed-set intersection in `CoalesceIsSafe`/`Combine`); the RA
class-intersection (kept as a now-rarely-exercised safety net).

## Phase 2 — migrate the cmp/eor explicit `Ac`-funnels — DONE (commit on main)

Migrated the two cmp funnels to the utility; left `EorByteWithImmediate`'s
destructive-EOR copy alone (it is the two-address materialization, not a funnel).
`EmitI8CmpBranch` / `EmitMultiByteCmpLadder` / `EmitCmp` / `SelectCondBr` became
instance methods (need `_registerInfo`); `EmitCmp` now builds the `cmp` with the
raw operands, calls `RegisterClassConstraining.ConstrainSelectedInstRegOperands`
(narrows use[0]→`Ac`, use[1]→`Imag8` in place; split copy only on conflict), then
`SetInsertionPointAfter(cmp)` so the caller's branches stay after it.

Net result (net win): if-else −4, CmpI8Branch −1, SelectMaterialized −2,
IncrementStrengthReduction −2 (the increment fold correctly stops firing for a
*compared* counter — without CPX/CPY a Y-counter must be TYA'd into $a per
iteration, so CLC/ADC in $a is strictly smaller; golden + comment updated to
document this, RUN-noadc removed). loop-counter regressed +2 (a 16-bit
accumulating loop — the only case where the funnel's manual live-range split
helped our weaker colourer). **Aggregate improved −7.8% → −8.4% (Irie 315 vs
llvm-mos 344, 17/47 paired).** All 200 Irie tests + 21 execution tests + full
solution suite green.

Gotcha for future regens: `SelectMaterialized-MachineCode.irie` interleaves
CHECK blocks with `func` defs (one pair per function); the simple
`/tmp/regen_golden.py` (single header/CHECK/trailer assumption) **deletes the
intermediate funcs** — use a per-func-block regen, and regenerate against the
**harness** `iriec` (`src/Irie.Tests/bin/.../iriec`), not the standalone dll.
Also: DotLit parses `[A-Z]+:` anywhere on a comment line as a directive, so prose
like `TYA: …` throws "Invalid command TYA" — avoid uppercase-word-then-colon in
`.irie` comments.

### Original notes (kept for reference)

The compare/eor lowering used to *always* emit a preserving `Ac` funnel copy,
where llvm splits only on conflict. Migrated these to the utility so a compare
operand that is dead-after needs no copy (llvm behaviour). High golden churn as
predicted.

Sites in `MOS6502InstructionSelector.cs` (line numbers approximate post-Phase-1):
- `EmitI8CmpBranch` (~667): builds an `aCmpVreg : Ac = pseudo.copy <a>` (~714) then
  `mos6502.cmp aCmpVreg, rhs`. Replace the manual funnel: pass the `a` operand vreg
  directly into the `cmp`, then call
  `RegisterClassConstraining.ConstrainSelectedInstRegOperands` on the built `cmp`.
  The utility inserts the `Ac` copy only if `a` is also needed in a disjoint class.
  Note the signed-slt path first runs `EorByteWithImmediate` (which has its own
  necessary destructive-EOR copy — leave that).
- `EmitMultiByteCmpLadder` (~852): same pattern, in the per-byte loop (the
  `aCmpVreg` creation ~912). Each byte's `cmp` should take the byte operand directly
  and be constrained.
- `EorByteWithImmediate` (~933): builds `aIn : Ac = pseudo.copy <byte>` then
  `eor.imm`. The EOR is genuinely destructive and tied (def[0] tied use[0]), so the
  copy IS needed (it's the two-address materialization, not a defensive funnel).
  **Likely leave EorByteWithImmediate as-is**; only the cmp funnels are defensive.
  Decide per-case: if the constrain utility + TwoAddressInstructionPass already
  produce the tie copy, the manual one is redundant.

`CmpInfo` operand classes (`MOS6502Dialect.cs`): use[0]=`Ac`, use[1]=`Imag8`,
implicit-def `$n,$z,$c`, no tied operands. So the utility will narrow use[0]→`ac`
(splitting only on conflict) and use[1]→`zp` — exactly the intent.

Steps:
1. For each site, build the `cmp`/`eor` with the raw operand vreg (drop the manual
   `Ac` `pseudo.copy`), capture the built instr, call the utility.
2. `dotnet test --project src/Irie.Tests` — regenerate every churned ASM golden
   under the harness (see below), then re-run.
3. `irie-report` — confirm aggregate doesn't regress (baseline this session: Irie
   317 vs llvm-mos 344, −7.8%, 17/47 paired). Ideally compares/eor cases improve
   (fewer always-on copies).
4. 21/21 emulator-execution tests (`dotnet test --project src/DotNetro.Compiler.Tests`).

Per CLAUDE.md: if dropping a funnel needs a special-case to suppress an artifact,
STOP — that signals the cmp lowering wants the copy for a real reason (e.g. the
value is live-after and the compare would otherwise clobber it — but `cmp` is
non-destructive, so it should be safe; verify, don't assume).

## Phase 3 — evaluate removing `InsertRelocationCopiesForConstrainedDefs` (NOT done)

`RegisterAllocatorPass.cs:599` (`InsertRelocationCopiesForConstrainedDefs`, called
at line 88) inserts a flexible `pseudo.copy` after every tied-def whose result
outlives the op, so the result can leave `$a`. In the llvm model this is the
coalescer + copy-prop's job, not a mandatory pre-pass. It is orthogonal to Phase 1
(it relocates tied *defs*, not the *uses* Phase 1 narrowed) so it was kept.

Plan:
1. Try removing the call at line 88 (and the method if unused).
2. `irie-report` + full lit suite + 21/21 execution tests.
3. **Risk:** Irie's graph-colouring allocator is weaker than llvm's greedy; without
   the relocation copy a tied-def result pinned `Ac` that outlives its op may force
   a spill the colourer can't avoid. If the aggregate regresses or any case spills,
   keep the pass (it is a legitimate Irie-specific aid, not a workaround to delete
   on principle). Decide by the scoreboard, not aesthetics.

## Regenerating goldens (harness-faithful)

`/tmp/regen_golden.py` (recreate if gone) re-runs each test's own `; RUN:` line
through the built `iriec.dll`, rebuilds the `; CHECK:` block as the complete
listing, escaping only `. $ ( ) # +` (house style; `{` stays literal), and keeps
the header (before first CHECK) + the input `func` trailer (after last CHECK).
Build first: `dotnet build src/Irie.Tools.Compiler/...`. Then e.g.
`python3 /tmp/regen_golden.py <test.irie> ...`. **Always re-verify under
`dotnet test --project src/Irie.Tests`** afterwards — a few frame-slot cases emit
a different-but-valid form standalone vs. under the harness (CLAUDE.md).

## Verification checklist (every phase)

- `dotnet test --solution src` (Debug; CI also runs Release) — all green.
- `dotnet test --project src/DotNetro.Compiler.Tests` — 21/21 (correctness/exec).
- `dotnet run --project src/Irie.Tools.Reference -- generate-report` — aggregate
  delta must not regress vs −7.8%; commit the regenerated
  `doc/irie/llvm-mos-comparison.md`.

See memory `project_constrain_operands_port.md` and `project_ra_redesign_corpus.md`
(scoreboard). Phase 1 commit is on `main`.
