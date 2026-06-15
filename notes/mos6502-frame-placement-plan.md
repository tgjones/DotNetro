# MOS6502 Frame Placement — Revised Plan (Item 2 redo)

Drafted 2026-06-15. Supersedes the frame-placement design in
[`mos6502-codegen-remaining-plan.md`](mos6502-codegen-remaining-plan.md) Item 2,
which shipped (commit `da9d9e0`) but regressed instruction counts (see the
`FrameSlotStructRoundtrip` analysis) by reusing the callee-saved register pool as
frame storage. This plan reworks frame placement to match how **llvm-mos**
actually structures the problem.

## Guiding principle: general-purpose elegance, not point hacks

The overriding goal of this rework is **general-purpose elegance**. We want each
pass to own one clean responsibility, expressed in terms of a correct underlying
model, rather than a stack of special-case rewrites that happen to produce the
right bytes for the corpus. Concretely:

- Prefer fixing the **representation** (how frame storage is modelled) over
  adding passes that pattern-match and undo an earlier pass's output.
- Decide **placement before lowering**, so each access is lowered correctly once
  — never "lower to the slow form, then detect-and-rewrite to the fast form".
- Keep the call-graph/coloring machinery **target-agnostic**, with the
  target supplying only its storage resources and costs (as llvm-mos does:
  generic `CallGraph`/SCC utilities, target-specific budget).
- Whenever a special case appears (escape, collision, dead-register cleanup),
  treat it as a smell that the model is wrong, and prefer a design where the
  special case **cannot arise** over one that detects and patches it.

The current `MOS6502StaticFrameAllocPass` violates all four; this plan removes
the violations rather than papering over them.

## Root cause of the regression (representational conflation)

Two coupled mistakes, both visible in
[MOS6502StaticFrameAllocPass.cs](../src/Irie/Target/MOS6502/MOS6502StaticFrameAllocPass.cs):

1. **Frame storage is modelled as RC *registers*, in the same namespace the RA
   uses.** Zp-placed slot bytes are emitted as `PhysicalReg(RC(n))` operands
   (`lda.zp RC(addr)`). RC20..RC31 are the **callee-saved register pool**
   ([MOS6502RegisterInfo.cs:129](../src/Irie/Target/MOS6502/MOS6502RegisterInfo.cs#L129)).
   So `PrologueEpilogueInsertionPass`, which saves every callee-saved register
   that appears as a *definition*
   ([PrologueEpilogueInsertionPass.cs:47-52](../src/Irie/Passes/PrologueEpilogueInsertionPass.cs#L47)),
   can't tell a frame byte from a preserved register and inserts `PHA`/`PLA` for
   it — the 8 extra instructions in the leaf case. The collision check
   (`CollectUsedRcRegisters`) and the dead-register sweep both exist only to
   manage this shared namespace.

2. **Placement is decided *after* lowering.** `FrameLoweringPass` (runs first)
   unconditionally materialises every slot as a `.bss` global and lowers accesses
   to indirect-Y; the post-RA `MOS6502StaticFrameAllocPass` then pattern-matches
   that lowered shape and rewrites it back to direct zp. This is the canonical
   "lower-then-rewrite" hack. It also *forces* the escape check
   (`SlotAddressesAreContained`): because the escaped address was already baked to
   the `.bss` global while in-function accesses get rewritten to zp, the slot
   would split across two locations — a problem that exists **only** because
   placement came after lowering.

## How llvm-mos structures it (the model to copy)

From reading `llvm/lib/Target/MOS/` (verified against `clang`/`llc` output):

- **Two disjoint zero-page namespaces.** The imaginary register file (`rc0..rcN`,
  the RA's registers) is one. Frame storage is a *separate* colored region — a
  single private global (`static_stack` in absolute memory by default;
  `zp_stack` when zp budget allows). Frame storage is **memory at an address**,
  never a register operand, so register-oriented passes never touch it.
- **Placement before lowering, default absolute.** `MOSStaticStackAlloc` assigns
  each statically-allocatable frame object an offset into the `static_stack`
  global; accesses are lowered against that decided location. Frames default to
  absolute RAM.
- **Zero page is an opportunistic, budgeted promotion.** `MOSZeroPageAlloc` is a
  *separate, benefit-ranked* pass that runs only when the platform declares free
  zp (`-zp-avail=N`, kept distinct from the register file: `assert ZPAvail <=
  256 - 32`). It promotes the highest-benefit frame objects / globals / CSRs into
  the `zp_stack` region, sized to the budget, leaving the rest in absolute RAM.
- **Coloring ⇒ no save/restore, ever, for frame storage.** SCC call-graph
  coloring lays each function's frame disjoint from all transitive callees', so a
  frame byte is never clobbered by a callee — preservation is unnecessary by
  construction. A genuine callee-saved *register* the RA parked a cross-call
  value in is the *only* thing that gets `PHA`/`PLA`, and even those can be
  demoted to a static home when beneficial.

The lesson: frame storage and the register file are different things living in
(possibly adjacent) zero page; conflating them is what broke us.

## Revised design

### 1. Model frame storage as addressed memory, not RC registers (the core fix)

A zp-placed frame access must lower to a load/store against a **zero-page
address** (a memory operand), not a `PhysicalReg(RC(n))`.

**What llvm-mos does here (verified in source).** It does **not** pick a zp-vs-
absolute *opcode* at instruction selection, and it has no dedicated "rewrite the
opcode to the zp form" pass. Instead:
- A **single logical load/store** op carries a generic **address operand**
  (`LDAbs`/`STAbs`/`LDImag8` over `addr16`/`addr8` in `MOSInstrLogical.td`).
- Zero-page-ness is a **property of the operand**, not the opcode:
  `canUseZeroPageIdx` ([MOSMCInstLower.cpp:33-46](file:///Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSMCInstLower.cpp))
  returns true iff the operand has the `MO_ZEROPAGE` target flag, *or* references
  a global whose `getAddressSpace() == AS_ZeroPage`, *or* is an in-range immediate.
- The concrete zp/abs **opcode byte is chosen at the very last step**
  (`MOSMCInstLower`, e.g. `LDAbs → LDA_ZeroPage`) from that operand property.
- The frame-placement pass (`MOSZeroPageAlloc`) **only sets the operand's storage
  attribute** — it mutates the global's type to `AS_ZeroPage`
  ([MOSZeroPageAlloc.cpp:352](file:///Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSZeroPageAlloc.cpp))
  or sets the frame object's `TargetStackID::MosZeroPage`. It never touches an
  opcode; the addressing mode falls out downstream for free.

**Decision (mirrors llvm-mos):** carry zp-ness as an **attribute on the address
operand/symbol**, use a **single load/store op**, and **select the zp opcode at
our existing late stage** — the post-RA `MOS6502AddressingModeSelectorPass`
(which already refines `mos6502.adc → .zp/.imm` from concrete operands) or the
emit table — keyed on the operand being a known-zp address. The placement pass
sets only the attribute (the `ZeroPage(addr)` placement on `FrameSlot`, step 2);
it never rewrites opcodes. This rejects both a bespoke zp-address operand variant
and the current pass's opcode-rewrite-after-the-fact approach.

Once frame storage is an address operand (not `PhysicalReg(RC(n))`),
`PrologueEpilogueInsertionPass`, liveness, RA, and the dead-register sweep all
stop seeing it as a register — **the collision check and the dead-register sweep
disappear entirely**, and the prologue/epilogue pass needs no change to become
correct.

### 2. Give each frame slot a decided placement (a `TargetStackID` analogue)

Extend `FrameSlot` (currently `(int Index, IRType Type, string SymbolName)` —
[MirFunction.cs:28](../src/Irie/Mir/MirFunction.cs#L28)) with a **placement**:
`Absolute` (the `.bss` global, today's default) or `ZeroPage(address)`. This is
the structural equivalent of llvm-mos's `MFI.setStackID(FI, MosZeroPage)` +
`setObjectOffset`. Placement is decided **before** the access is lowered.

### 3. Frame-placement + coloring pass (target-agnostic core, target hook)

Replace the post-RA `MOS6502StaticFrameAllocPass` with a placement pass that runs
**before** `FrameLoweringPass`, structured as:

- **Target-agnostic core**: `ReentrancyAnalysis` (already exists, already generic)
  + the SCC/bottom-up static-stack coloring (`base(f) = max over callees of
  base(c)+footprint(c)`), producing per-slot offsets within a colored region.
  This is the reusable algorithm; keep it in `Passes/`.
- **Target hook** (a `FreeZeroPage` budget property on `Target`): declares the
  available zp frame range (a *separate* zp range from the RC register file —
  not RC20..29; `$70`–`$8F` on the BBC Micro subtarget, 0 on the base chip) and
  the absolute fallback. Promotion is greedy per-function all-or-nothing (see the
  resolved cost-model decision below); no frequency scoring for now.

Default placement is `Absolute`; slots are promoted to `ZeroPage` only up to the
declared budget. Coloring guarantees disjointness, so no save/restore is ever
needed for either placement.

Because placement is decided up front, an **address-taken slot keeps a single
location** (its decided address, zp or absolute) everywhere — so the escape check
is no longer needed to *prevent* a split; at most the cost model may *prefer*
absolute for address-taken slots, but correctness no longer depends on it.

### 4. FrameLowering lowers per placement — one lowering, no rewrite

`FrameLoweringPass` consults each slot's placement: `Absolute` → today's `.bss`
global + indirect-Y; `ZeroPage(addr)` → direct `lda.zp/sta.zp <addr>` against the
addressed-memory representation from (1). No second pass, no opcode rewrite, no
dead-register sweep.

### 5. Prologue/epilogue and the RA window are untouched

With frame storage out of the RC namespace, RC20..RC31 revert to their single
purpose: the RA's callee-saved pool for genuine cross-call *values*, preserved by
`PHA`/`PLA`. `PrologueEpilogueInsertionPass` is already correct for that and needs
no special-casing. (A later, llvm-mos-style refinement — demoting a preserved RC
register to a static zp home to skip its save/restore — becomes a natural,
*optional* extension of the same placement pass, not a prerequisite.)

## What this deletes (the hack inventory)

- `CollectUsedRcRegisters` / per-function RC collision check → gone (separate
  namespace).
- `SlotAddressesAreContained` escape check → gone as a *correctness* gate
  (placement-first keeps slots single-location); survives at most as a cost
  preference.
- `RewriteSlotAccesses` indirect-Y→zp opcode rewrite → gone (lowered correctly
  once).
- `DeadRegisterSweep` + `ComputeBlockLiveOut` post-hoc cleanup → gone (no
  orphaned pointer setup is ever created).
- No `PrologueEpilogueInsertionPass` change needed to stop the spurious `PHA`/`PLA`.

The net is *less* code than the shipped pass, and each remaining piece has one
responsibility.

## Pass ordering

Frame placement must run **before** `FrameLoweringPass` (so lowering sees the
decided placement), and it needs the call graph. Today's pipeline
([Program.cs:63-78](../src/Irie.Tools.Compiler/Program.cs#L63), mirrored in
`CompilerDriver`) starts `FrameLowering → AbiLowering → …`. New order:

```
ReentrancyAnalysis → FramePlacement(+coloring) → FrameLowering → AbiLowering → …
                                                  (post-RA passes lose StaticFrameAlloc)
```

`MOS6502StaticFrameAllocPass` is removed from `AddPostRegisterAllocationPasses`
([MOS6502Target.cs:27](../src/Irie/Target/MOS6502/MOS6502Target.cs#L27)). Note
the call graph here is over **pre-isel** call ops (`call.func @name`) rather than
the post-RA `mos6502.jsr.abs @name` the current pass reads — an argument for the
placement pass living early and target-agnostic.

## Resolved decisions (was: open questions)

All four prior open questions are decided. Execute against these; no further
research needed.

- **Operand representation — DECIDED.** Single load/store op + address operand
  carrying a zp attribute; zp opcode chosen late (AMS / emit table); placement
  pass sets only the attribute. See step 1 above — this matches llvm-mos exactly
  and needs no bespoke operand variant and no opcode-rewrite pass.

- **Zp frame region + budget — DECIDED: `$70`–`$8F` (32 bytes) on the BBC Micro
  subtarget.** Consistency note first: frame storage and the RC register file are
  the *same kind of thing* — RAM-resident zero page our program clobbers for its
  whole lifetime — so they must share one assumption about which zp is ours, and
  may NOT have different safety rules. The RC file already lives at `$00`–`$1F`
  and the runtime at `$37`–`$3B`, both inside BASIC's `$00`–`$6F` workspace; that
  placement is only valid because our program assumes it **owns low zero page**
  (BASIC not required during execution). Under that same already-baked-in
  assumption, `$00`–`$6F` is equally available to frames — so `$70`–`$8F` is **not**
  chosen to "avoid BASIC" (we don't, for registers). It is chosen as a clean
  dedicated block disjoint from the RC file and runtime, that happens to be
  *strictly safer* than where the registers already sit (`$70`–`$8F` is the BBC
  Micro user zero page, safe even with BASIC resident — verified against the BBC
  Advanced User Guide / mdfs.net, not the DotNetro codebase). It is a conservative
  *floor*: if 32 bytes is ever tight, the gaps at `$20`–`$36` and `$3C`–`$6F` are
  available under the register pool's existing assumption. Wire it as a
  **subtarget property** (e.g. `FreeZeroPage` on `Target`, overridden only by
  `MOS6502BbcMicroTarget`, exactly as it overrides `DefaultOrigin`/`PackageImage`):
  - Base `MOS6502Target` (bare chip, no platform): budget **0** → all frames
    `Absolute`. No platform commitment baked into the chip target.
  - `MOS6502BbcMicroTarget`: budget = `$70`–`$8F` (32 bytes).
  - Overflow (a call path needing > 32 frame bytes) and budget-0 → `Absolute`
    fallback. Correctness/elegance never depend on the budget; only the speedup
    does.
  - **To confirm (don't trust the codebase):** the "program owns low zero page /
    BASIC not required" assumption is *inferred* from the RC file's `$00`–`$1F`
    placement, not verified. It doesn't affect the `$70`–`$8F` choice (safe either
    way), but confirm the actual invocation model before relying on low zp — and
    if it turns out BASIC must be preserved, the RC register pool is already wrong
    and needs addressing independently of this plan.

- **Cost model — DECIDED: greedy by coloring, per-function all-or-nothing.**
  Run the bottom-up static-stack coloring (`base(f) = max over callees of
  base(c)+footprint(c)`). A function's slots are promoted to zp iff its whole
  frame fits the 32-byte window at its colored base; otherwise that function's
  slots stay `Absolute` (footprint 0, so it reserves no window — matching the
  current `Resolve`/footprint logic, just over the `$70`–`$8F` region instead of
  RC20..29). No frequency/benefit scoring — llvm-mos's `BlockFrequencyInfo`
  ranking is a deferred refinement, not needed for the corpus.

- **RA interaction — DECIDED: none; the RA is untouched.** Because `$70`–`$8F` is
  outside the RC namespace, frame storage is never a register operand, so the RA
  needs no reserved-register mechanism (it has none today and now needs none) and
  keeps its full `RC2`–`RC31` allocatable pool — removing frame storage from
  RC20..29 only *reduces* RA pressure. Frame survival across a call is guaranteed
  by coloring (a callee's frame is disjoint), not by callee-saving, so frame
  bytes need no `PHA`/`PLA` regardless. Address-taken slots may be zp-promoted:
  placement-first gives them a single location ($70.. or absolute), so the
  pointer materialised from the slot symbol just resolves to that one address.

## Migration / sequencing (keep the suite green at each step)

1. Add the addressed-memory zp representation (1) and teach `FrameLoweringPass`
   to emit it for a `ZeroPage` placement — but keep every slot `Absolute` so
   output is unchanged. Verify suite green (pure plumbing).
2. Add `FrameSlot` placement (2) + the target-agnostic placement/coloring pass
   (3) wired before `FrameLowering`, with a **zero budget** so still all
   `Absolute`. Suite green, no codegen change.
3. Set the `MOS6502BbcMicroTarget` budget to `$70`–`$8F` (base `MOS6502Target`
   stays 0); slots that fit promote to zp. Now `FrameSlotStructRoundtrip` should
   show direct `lda.zp/sta.zp` at `$70..` **and no `PHA`/`PLA`** for the leaf.
   Regenerate goldens (genuine improvement). NB: the existing
   `FrameSlotStructRoundtrip` lit test runs `--target mos6502` (base chip,
   budget 0) — to exercise zp promotion, point it (or a sibling) at
   `--target mos6502-bbcmicro`.
4. Delete `MOS6502StaticFrameAllocPass` and its hacks once (1)–(3) cover its
   cases. Confirm `StaticFrameAllocChain` / `Overflow` / `Recursive` behaviours
   are preserved by the new pass (port the tests).

## Tests

Reuse and re-point the existing lit tests:
- `FrameSlotStructRoundtrip` — leaf: assert direct `lda.zp/sta.zp` **and no
  `PHA`/`PLA`** (the regression that motivated this plan).
- `StaticFrameAllocChain` — coloring: caller's frame disjoint from callee's,
  independent subtrees reuse the same zp.
- `StaticFrameAllocOverflow` — budget exceeded → `Absolute` fallback, still
  correct.
- `StaticFrameAllocRecursive` — reentrant function stays `Absolute`.
- An address-taken-slot test confirming single-location correctness without the
  escape hack.
- Emulator end-to-end (`UseStructWithConstructor`) stays green.
