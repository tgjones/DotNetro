# MOS6502 Frame Placement — Implementation Notes (concrete representation)

> **SUPERSEDED (2026-06-16).** This file documents the *place-before-lower*
> representation that shipped in `5c02ddb`…`74b2ce4`. That architecture leaked
> 6502 specifics into target-agnostic code and placed frames too early; it was
> reworked to place late (post-RA) and keep frame accesses abstract. See
> [`mos6502-frame-placement-rework-plan.md`](mos6502-frame-placement-rework-plan.md)
> (status: COMPLETE) for the current design. Kept for history only — the facts
> below no longer match the codebase.

Companion to [`mos6502-frame-placement-plan.md`](mos6502-frame-placement-plan.md).
The plan fixes the *design*; this file fixes the *concrete representation* in our
codebase so each implementation commit is self-contained. Read the plan first.

## Key facts about the current code (verified 2026-06-15)

- A frame slot flows: `mem.frame_addr <i>` → (FrameLoweringPass) `mem.symbol @slot`
  → (legalizer narrows wide load/store) `mem.load.byte_at` / `mem.store.byte_at`
  → (MOS6502InstructionSelector) **indirect-Y** sequence: materialise the slot's
  16-bit address into the RC0/RC1 zero-page pointer pair via
  `lda.imm.symlo/.symhi @slot`, set `$y` to the byte offset, then `lda.indy` /
  `sta.indy`. This is the only lowering today, used for *both* absolute globals
  and frame slots.
- `FrameSlot` is `record (int Index, IRType Type, string SymbolName)`
  ([MirFunction.cs] holds `List<FrameSlot> FrameSlots`). The list **persists** on
  the function after FrameLowering (FrameLowering reads it but never clears it),
  so passes *and the instruction selector* can consult it later via
  `builder.Function.FrameSlots`.
- `MirGlobal` is `record (string SymbolName, IRType Type, int SizeInBytes,
  MirInitializer? Initializer)`. Zero-init (`Initializer is null`) globals are
  `.bss`-style.
- The MachineCode emit table maps each `MOS6502Op` → `EmitRule(opcodeByte,
  EmitOperandKind, operandIndex)`. `EmitOperandKind.ZeroPageAddress` currently
  reads a **`PhysicalReg(RC(n))`** at the given index and emits the address byte
  `phys.Id - RC(0)` (so an RC register's zp address *equals* its index, 0..31).
  `MOS6502MachineCodeEmitter.EmitZeroPage` enforces "must be a PhysicalReg".
- The binary encoder lays globals out **after all functions** in the absolute
  region and (for zero-init) emits zero bytes that advance the cursor. Symbols
  resolve through `_globalAddrs` / `_functionAddrs`. `ZeroPage` addressing mode
  encodes a 1-byte operand and **already accepts `MachineCodeOperand.Immediate`**
  (see `EncodeOneByteOperand`).
- `MOS6502Op.LdaZp` / `StaZp` carry `DialectInstructionInfo.Empty` (no operand
  classes, no tied operands) — RA treats their operands generically.
- Pipeline order (both `CompilerDriver` and `iriec` Program.cs):
  `FrameLowering → AbiLowering → Legalizer → InstructionSelector → PhiElimination
  → TwoAddress → RegisterAllocator → CopyElimination →
  [AddressingModeSelector, IncrementStrengthReduction, ParallelCopy,
  StaticFrameAlloc] → PrologueEpilogueInsertion → PseudoExpansion → …`.
  `CompilerDriver` uses `MOS6502BbcMicroTarget`; the `FrameSlotStructRoundtrip`
  lit test uses base `--target mos6502`.
- **Naming caution:** `Target.FrameLowering` / `MOS6502FrameLowering` is the
  *prologue/epilogue* (callee-saved spill) hook used by
  `PrologueEpilogueInsertionPass`. It is **unrelated** to `FrameLoweringPass`
  (which materialises FrameSlots into globals). Do not conflate them.

## Representation decisions (locked)

### D1. `FrameSlot.Placement`
Add an abstract record:
```csharp
public abstract record FrameSlotPlacement
{
    public sealed record Absolute : FrameSlotPlacement;
    public sealed record ZeroPage(int Address) : FrameSlotPlacement;
    public static readonly FrameSlotPlacement Default = new Absolute();
}
```
Keep `FrameSlot`'s positional params `(Index, Type, SymbolName)` unchanged (the
MIR text/binary parsers construct it positionally). Add placement as a mutable
property so the placement pass can set it after construction:
`public FrameSlotPlacement Placement { get; set; } = FrameSlotPlacement.Default;`
Placement is **computed by a pass**, never serialised in MIR text.

### D2. `MirGlobal.ZeroPageAddress`
Add `public int? ZeroPageAddress { get; init; }` (default `null`) as a
non-positional member so existing `new MirGlobal(name, type, size, init)` sites
are untouched. `null` = absolute (today's behaviour). Non-null = the global is a
**fixed zero-page reservation**: the binary encoder records its symbol address as
that zp address but emits **no bytes** and does **not** advance the absolute
cursor (it lives in pre-existing zero-page RAM, far below `origin`). This is the
`AS_ZeroPage` + concrete-offset analogue from llvm-mos.

### D3. Zero-page frame access = direct `lda.zp` / `sta.zp` with an **Immediate** address
A zp-placed slot's in-function byte access lowers (at instruction selection) to a
**direct** zp load/store carrying the *concrete literal zp address*
(`base + byteOffset`) as a `Mir.Immediate` operand — no pointer pair, no `$y`,
no indirect-Y. Rationale: byte offsets (0..3 for a 4-byte struct) are mandatory
and trivial as a literal; `Immediate` reuses the existing `ZeroPage` encoding;
and "a zp address named by a literal" is exactly the plan's *addressed-memory*
model (vs. "a zp address named by an RC register index", which the register file
legitimately uses). A zp address operand is therefore polymorphic over
`PhysicalReg(RC)` and `Immediate`.

Concretely extend `MOS6502MachineCodeEmitter.EmitZeroPage` to also accept a
`Mir.Immediate` at the operand slot, emitting `MachineCodeOperand.Immediate(value)`
(the encoder already handles it). `LdaZp`/`StaZp` keep `DialectInstructionInfo.Empty`,
so building them with an `Immediate` address operand + an `Ac` vreg for the data
needs no new dialect metadata.

Emitted shapes (mirror the existing indirect path's `$a`-funnelling so RA stays happy):
- load:  `%ac : ac = mos6502.lda.zp #<addr>` then `%any : anyi8 = pseudo.copy %ac`.
- store: park value → `%src : ac = pseudo.copy %val`, then
  `mos6502.sta.zp #<addr>, %src`. (StaZp emit rule reads operand index 0 as the
  zp address and the source is `$a`; emit operands `[Immediate(addr), srcAcVreg]`.)

### D4. Address-taken zp slots
`lda.imm.symlo/.symhi @slot` (address materialisation) is unchanged; it resolves
through the encoder to the global's `ZeroPageAddress` (low byte = the zp address,
high byte = 0). So a zp-promoted address-taken slot has a single location and
needs no escape check. (The greedy cost model does not special-case
address-taken; correctness holds either way.)

### D5. Instruction selector keys on the FrameSlot placement
In `SelectMemLoadByteAt` / `SelectMemStoreByteAt`, after resolving the address to
a symbol name, look it up in `builder.Function.FrameSlots`. If a matching slot has
`Placement is FrameSlotPlacement.ZeroPage zp`, emit the **direct** form (D3) with
`Immediate(zp.Address + offset)` and skip `EmitPointerSetup` / the indirect-Y
emit entirely; still call `TryRemoveDeadMemSymbol`. Otherwise fall through to
today's indirect-Y path verbatim. (The `_currentPointerKey` cache is only touched
on the indirect path, so the direct path must not disturb it.)

## Target-agnostic placement pass (design plan §3, migration §2)

`StaticFramePlacementPass` in `Passes/` (target-agnostic), wired **before**
`FrameLoweringPass`. Reuses `ReentrancyAnalysis` and a structural pre-isel call
graph (edges from `call.func @name`; reuse the same "Symbol operand on a
non-side-effect-free op" detection `ReentrancyAnalysis` uses — at this point the
call op is `call.*`, not yet `mos6502.jsr.abs`). Algorithm (greedy bottom-up
static-stack colouring, identical maths to today's `MOS6502StaticFrameAllocPass`
but over a byte address window, not RC indices):

```
size(f)      = Σ slot.Type.SizeInBits/8 over f's slots
base(f)      = max over direct callees c of base(c) + footprint(c)   (leaf → 0)
footprint(f) = size(f) if f non-reentrant AND base(f)+size(f) ≤ budget else 0
```
A function whose `footprint > 0` promotes **all** its slots: slot i at
`window.Start + base(f) + offsetWithinFrame(i)`, set
`slot.Placement = ZeroPage(addr)`. Otherwise leave `Absolute`. No collision check
(separate namespace), no escape check, no opcode rewrite.

The window comes from a new `Target` property:
```csharp
public virtual FrameZeroPageWindow FreeZeroPage => FrameZeroPageWindow.None; // base = empty
```
`FrameZeroPageWindow(int Start, int Size)` (`None` = Size 0). `MOS6502BbcMicroTarget`
overrides it to `new(0x70, 0x20)` (i.e. `$70`–`$8F`, 32 bytes), exactly as it
overrides `DefaultOrigin`/`PackageImage`. Base `MOS6502Target` keeps `None` → all
`Absolute`. The pass needs the window; thread it in via the pass constructor
(the pass is generic, the *budget* is the target's — pass the `FreeZeroPage`
value into `new StaticFramePlacementPass(target.FreeZeroPage)` at pipeline build).

## What gets deleted (plan "hack inventory"), in the final commit
- `MOS6502StaticFrameAllocPass` (whole file) + its removal from
  `MOS6502Target.AddPostRegisterAllocationPasses`.
- With it: `CollectUsedRcRegisters`, `SlotAddressesAreContained`,
  `RewriteSlotAccesses`, `DeadRegisterSweep`, `ComputeBlockLiveOut`.

## Commit plan (suite green at each; commit to main after each)
- **A — representation plumbing (dormant):** D1, D2, D3, D4-encoder, D5. All slots
  stay `Absolute` (no pass sets `ZeroPage` yet) ⇒ output unchanged. Suite green.
- **B — placement pass, zero budget:** add `FreeZeroPage` (base `None`),
  `FrameZeroPageWindow`, `StaticFramePlacementPass` wired before FrameLowering in
  both pipelines. Base budget `None` ⇒ everything `Absolute`. Suite green, no
  codegen change.
- **C — BBC budget + promotion:** `MOS6502BbcMicroTarget.FreeZeroPage = (0x70,
  0x20)`. Remove `MOS6502StaticFrameAllocPass` from the post-RA pass list (the new
  early pass now owns placement; leaving both active would double-promote).
  Re-point `FrameSlotStructRoundtrip` (and/or a sibling) to
  `--target mos6502-bbcmicro`; regenerate its golden to direct `LDA $70`/`STA $70`
  **with no PHA/PLA**. Port `StaticFrameAllocChain/Overflow/Recursive` +
  add an address-taken-slot test. Confirm the DotNetCompilerTests emulator suite
  (`UseStruct*`, which compiles via `MOS6502BbcMicroTarget`) stays green.
- **D — delete dead code:** remove `MOS6502StaticFrameAllocPass.cs` and confirm no
  dangling references; final suite green.
```
