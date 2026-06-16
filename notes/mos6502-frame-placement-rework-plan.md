# Frame Placement Rework — remove target leaks, place late (llvm-mos-faithful)

Drafted 2026-06-15. Corrects the architecture shipped in commits `5c02ddb`,
`447a0d0`, `b24e2ff`, `74b2ce4` (the "Item 2 redo"), which leaked 6502-specific
concepts into target-agnostic code and ran frame placement far too early.
Supersedes [`mos6502-frame-placement-impl-notes.md`](mos6502-frame-placement-impl-notes.md).

## What llvm-mos does (verified in source, not memory)

- **Placement is post-register-allocation.** `MOSZeroPageAlloc` runs in
  `addPrePEI()`; `MOSStaticStackAlloc` in `addPreSched2()`
  ([MOSTargetMachine.cpp](../../llvm-mos/llvm/lib/Target/MOS/MOSTargetMachine.cpp),
  ~L306, L322). Both are *after* the register allocator. This is mandatory, not
  incidental: **spill slots are frame objects that don't exist until RA runs**,
  so any pass that colours/place frames must run after RA to see them. (DotNetro
  already anticipates this — see the `pseudo.spill`/`pseudo.reload` comment in
  [`Dialects/Pseudo/PseudoOp.cs`](../src/Irie/Dialects/Pseudo/PseudoOp.cs).)
- **Frame accesses stay abstract until late.** An access carries a *frame-index*
  operand + offset throughout. The concrete address is materialised only in
  `MOSRegisterInfo::eliminateFrameIndex` (post-RA, inside PEI), which reads the
  frame object's stack ID + offset and rewrites the operand
  ([MOSRegisterInfo.cpp](../../llvm-mos/llvm/lib/Target/MOS/MOSRegisterInfo.cpp)
  L259-300): `MosZeroPage` → `ZeroPageStackValue` global + offset; `MosStatic` →
  `TI_STATIC_STACK` + offset.
- **The placement passes set attributes only.** `MOSZeroPageAlloc` does
  `MFI.setStackID(FI, MosZeroPage)` — never an opcode/address rewrite. The zp
  *opcode* is chosen last, at MCInstLower, from the stack ID
  ([MOSMCInstLower.cpp](../../llvm-mos/llvm/lib/Target/MOS/MOSMCInstLower.cpp) L881).
- **The data model is generic-but-opaque.** Frame objects carry a `TargetStackID`
  — a generic `MachineFrameInfo` field. `MosZeroPage`/`MosStatic` are *target-
  defined values*; the mechanism (opaque stack-id int + object offset) is generic.
  The zp budget (`-zp-avail`) lives entirely inside `MOSZeroPageAlloc`; the generic
  `TargetFrameLowering` base has no zero-page concept.

## Root cause of the leaks

DotNetro's `FrameLoweringPass` lowers slots to globals + indirect-Y *before the
legalizer*, destroying the abstract slot identity early. To "decide placement
before lowering" I put placement in front of that early lowering, which forced
the zp decision + concrete address + budget to be visible early — hence the four
leaks. The fix is to mirror llvm-mos: **keep frame accesses abstract, place late
(post-RA, target-owned), and choose the addressing mode last.**

## Target architecture (faithful)

### Generic (target-agnostic) — opaque only
- **`FrameSlot`**: replace `Placement : FrameSlotPlacement` with two opaque fields
  `int StackId` (default `FrameSlot.DefaultStackId = 0`) and `int Offset`. These
  are the `TargetStackID` + `getObjectOffset` analogues. Generic code never
  interprets a non-default stack id.
- **`StaticFrameColorer`** (Passes/, pure helper, *not* a wired pass): the reusable
  bottom-up SCC colouring (`base(f)=max over callees of base(c)+footprint(c)`),
  parameterised by `(eligibility, perFunctionFootprint, budgetSize)` → returns each
  promoted function's `base`. `ReentrancyAnalysis` stays generic. No zp, no budget
  value baked in — the caller (a target pass) supplies them.
- **Frame-access lowering hook**: extend the existing `Target.FrameLowering`
  abstraction (the PEI hook) with `LowerFrameAccess(...)`, the `eliminateFrameIndex`
  analogue. A generic post-RA `FrameAccessLoweringPass` walks abstract frame
  accesses and delegates each to the target hook. Wire it post-RA, *after* the
  target's placement pass and before `PseudoExpansion`.

### MOS6502 (target-private) — all zp specifics
- `MOS6502FrameStackIds.ZeroPage = 1` (private const; default 0 = absolute/static).
- The zp window `$70–$8F` is a private const in the placement pass (base chip: no
  window → never promotes). No `Target.FreeZeroPage`, no `FrameZeroPageWindow`.
- **`MOS6502FramePlacementPass`** — target-specific, added via
  `AddPostRegisterAllocationPasses` (so it runs post-RA), *before* the generic
  `FrameAccessLoweringPass`. Runs `ReentrancyAnalysis`, invokes `StaticFrameColorer`
  with its private window + eligibility, and sets `slot.StackId = ZeroPage;
  slot.Offset = base+within` for promoted slots. Owns the budget. This is the
  spiritual return of the old `MOS6502StaticFrameAllocPass` — but *without* the RC
  conflation, escape check, or opcode rewrite, because placement now precedes
  addressing instead of following it.
- **`MOS6502FrameLowering.LowerFrameAccess`**: per the slot's StackId — `ZeroPage`
  → direct `lda.zp/sta.zp #(windowBase + slot.Offset + byteOffset)`; default →
  today's absolute global + indirect-Y. The concrete zp address is computed here,
  late, from the opaque offset + the target's private window base.

### Keeping frame accesses abstract until post-RA
The one structural change in the pre-RA pipeline. Today `mem.frame_addr` →
`mem.symbol @slot` (early) and isel commits frame-slot byte accesses to indirect-Y.
Instead:
- Keep `mem.frame_addr` → `mem.symbol @slot` early (the symbol *is* the frame
  object's identity, like a named frame index) and keep the generic legalizer
  narrowing wide loads/stores to byte accesses (unchanged).
- At **isel**, a byte access whose address symbol is a `FrameSlot` lowers to an
  **addressing-mode-agnostic** MOS op — `mos6502.frame.load.byte @slot, #off` /
  `mos6502.frame.store.byte @slot, #off` — that routes the value through `$a`
  (so RA models it) and declares its scratch clobbers (`$y`, `$rc0`, `$rc1`) as
  implicit defs (so RA reserves them), but does **not** pick zp-vs-indirect. Non-
  frame globals (strings, statics) keep today's indirect-Y at isel — they are
  genuine absolute addresses, not frame objects.
- **Post-RA**, after placement, `FrameAccessLoweringPass` → `LowerFrameAccess`
  expands each `mos6502.frame.*.byte` to the concrete sequence per StackId. The
  value is already in `$a` and the scratch regs are already reserved, so no
  register scavenging is needed (mirrors how llvm-mos's LDStk/STStk model their
  register effects for RA).

## What this deletes / changes
- DELETE: `FrameSlotPlacement` (+ `.ZeroPage`), `MirGlobal.ZeroPageAddress`,
  `MachineCodeGlobal.ZeroPageAddress`, `Target.FreeZeroPage`,
  `FrameZeroPageWindow`, the early generic `StaticFramePlacementPass`.
- The `EmitZeroPage` "accept a literal Immediate" extension (commit `5c02ddb`)
  stays — `LowerFrameAccess` still emits `lda.zp #addr`; that's a legitimate
  "zp address named by a literal", just produced late by the target now.
- KEEP: `ReentrancyAnalysis` (generic), the colouring maths (now in the generic
  `StaticFrameColorer` helper, invoked by the target pass).

## Staging (keep the suite green; commit per stage)
1. **Generic opaque model + colorer helper.** Replace `FrameSlotPlacement` with
   `StackId/Offset`; extract `StaticFrameColorer`. Temporarily keep the *current*
   early placement+lowering working against the new fields (MOS still reads
   StackId early) so behaviour/goldens are unchanged. Removes objection #4 (the
   generic `ZeroPage` type) without yet re-timing.
2. **Abstract frame ops + late lowering, default-only.** Add the
   `mos6502.frame.*.byte` ops, the `LowerFrameAccess` hook + generic
   `FrameAccessLoweringPass` (post-RA), and route frame accesses through them with
   StackId=default (absolute/indirect-Y) for *all* slots. Drop the early placement
   pass and `MirGlobal.ZeroPageAddress`. bbcmicro goldens temporarily revert to
   absolute (regenerate). Suite green. Removes objections #1, #2 (timing + global
   leak).
3. **Target-private post-RA placement.** Add `MOS6502FramePlacementPass`
   (post-RA, owns the `$70–$8F` window via `StaticFrameColorer`), set zp StackId;
   `LowerFrameAccess` honours it. Remove `Target.FreeZeroPage`/`FrameZeroPageWindow`.
   Regenerate bbcmicro goldens back to direct zp (matching commit `b24e2ff`'s
   output: direct `lda.zp/sta.zp`, no PHA/PLA). Suite green. Removes objection #3.
4. **Sweep.** Confirm no generic file references zero page; emulator suite green;
   update the impl-notes / this plan's status.

## Tests
Same matrix as the prior rework (leaf no-PHA/PLA, chain colouring, overflow
fallback, recursive stays absolute, address-taken single-location, emulator
end-to-end), re-pointed as needed. Add a check that a *post-RA* dump (e.g.
`--stop-after` the placement pass) shows the StackId attribute set, proving
placement happens late.

## Open decision (recommend the faithful option)
The full re-timing (stage 2-3) is the bulk of the work; the data-model de-leak
(stage 1) is mostly mechanical. Recommendation: do all four — a partial de-leak
that still places early would keep DotNetro structurally divergent from llvm-mos
and re-leak the moment spill slots (which only exist post-RA) need placing.

## Status — COMPLETE (2026-06-16)
All four stages landed; `dotnet test --solution src` green (201 tests, incl. the
emulator end-to-end suite). Commits:
- Stage 1 `5f05a87` — opaque `FrameSlot.StackId/Offset` + extracted
  `StaticFrameColorer`; pure refactor, goldens unchanged. (Objection #4.)
- Stage 2 `a8b54a7` — abstract `mos6502.frame.{load,store}.byte` ops +
  `Target.FrameLowering.LowerFrameAccess` + generic post-RA
  `FrameAccessLoweringPass`; deleted the early placement pass and
  `MirGlobal/MachineCodeGlobal.ZeroPageAddress`. (Objections #1, #2.)
- Stage 3 `c1adf48` — target-private post-RA `MOS6502FramePlacementPass` (private
  `$70–$8F` window via `StaticFrameColorer`); `LowerFrameAccess` emits direct
  `lda.zp/sta.zp` for promoted slots, indirect-Y for the absolute default. Removed
  `Target.FreeZeroPage`/`FrameZeroPageWindow`. (Objection #3.)

### Deviations from this plan (decided during implementation)
1. **Store value lives in `$a`, not `$x`.** The plan said frame ops "route the
   value through `$a`"; for the store that is exactly right, but the absolute
   indirect-Y expansion would clobber `$a` while materialising the pointer. The
   faithful fix is to build that pointer via `$x` (new `ldx.imm.sym{lo,hi}` ops)
   so `$a` survives to `sta.indy` — no `txa`, and the zp path is the plan's clean
   `LDA #imm; STA $70`. (An earlier cut pinned the value to `$x` and paid a `txa`;
   replaced.)
2. **Address-taken slots ARE promoted (no escape check).** The plan asserted
   "no escape check … keeps every slot single-location"; that held only under the
   old place-before-lower design. With late lowering, an escaped address is
   materialised from the slot's `.bss` symbol at isel, which the late zp pass
   never rewrites — so a naive promotion splits the slot into two homes (a real
   miscompile, caught by the struct-ctor emulator test). Rather than refuse to
   promote escaping slots (conservative; regresses vs `b24e2ff` and diverges from
   llvm-mos), we mirror llvm-mos `eliminateFrameIndex`: the placement pass pins the
   promoted slot's global to its zp address via the **generic** `MirGlobal.FixedAddress`
   (a zero-storage symbol alias, set late), so every reference — direct access and
   escaped address alike — resolves to the same zero-page byte. `FixedAddress` is a
   target-agnostic "pinned, no-storage" concept, not the early zp-named field
   Stage 2 deleted.

### Sweep
No generic (non-`Target/MOS6502/`) file contains a zero-page **type, field, or
branch** — the only zero-page mentions in generic code are explanatory comments
using MOS6502 as an illustrative example (the codebase's established style). The
structural additions to generic code are the opaque `FrameSlot.StackId/Offset`,
the generic `MirGlobal/MachineCodeGlobal.FixedAddress`, and the
`Target.FrameLowering.LowerFrameAccess` hook + `FrameAccessLoweringPass`.
