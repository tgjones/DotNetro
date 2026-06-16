# Plan: absolute addressing for symbol-addressed loads/stores (MOS6502)

## Context

The `global-rw` reference case is the worst Irie-vs-llvm-mos divergence in the
codegen scoreboard (Irie 29 instructions vs llvm-mos 10). The C is a
read-modify-write of a global:

```c
int counter;
int global_rw(int delta) { counter += delta; return counter; }
```

llvm-mos uses **absolute addressing** throughout — the global's address is a
compile-time constant, so no register or zero-page pointer is wasted on it:

```asm
clc
adc counter          ; load+add folded into one absolute-addressed ADC
tay
txa
adc counter+1
tax
tya
sty counter          ; absolute store
stx counter+1
rts
```

Irie instead materialises a **runtime zero-page pointer** to the global and uses
indirect-indexed addressing for every byte, even though the address is static:

```asm
LDA #<counter        ; build a zp pointer at $00/$01 …
STA $00
LDA #>counter
STA $01
LDY #$00             ; … then indirect-Y load each byte
LDA ($00),Y
…
STA ($00),Y
```

That pointer-setup + `LDY` overhead is pure waste for a statically-known address.
This plan makes Irie use absolute addressing for symbol-addressed accesses,
mirroring llvm-mos.

## llvm-mos reference (how it does this)

Verified against `ext/llvm-mos-reference/memory/global-rw.{s,txt}` and the
llvm-mos source:

1. **Legalizer picks the addressing mode** —
   `MOSLegalizerInfo::selectAddressingMode` → `tryAbsoluteAddressing`
   (`llvm/lib/Target/MOS/MOSLegalizerInfo.cpp:1670-1751`). `matchAbsoluteAddressing`
   walks the address chain — a `G_GLOBAL_VALUE`, an `iconstant`, a statically
   allocated `G_FRAME_INDEX`, or a `G_PTR_ADD base + const` — and if it resolves
   to a static address it rewrites `G_LOAD/G_STORE` → `G_LOAD_ABS/G_STORE_ABS`
   with a single GA operand carrying `(global, offset)`; the pointer register is
   dropped entirely. The 16-bit access is then narrowed to two byte `G_LOAD_ABS
   @counter` / `@counter+1` (the `+offset` on the global is how the high byte is
   addressed — `.txt` line ~1642).
2. **Selector folds an absolute load into the ALU operand** — `m_FoldedLdAbs`
   (`MOSInstructionSelector.cpp:343-356`, gated by `shouldFoldMemAccess`: single
   use, no aliasing) matches a `G_LOAD_ABS` feeding `adc`/`sbc`/`cmp` and selects
   the absolute-addressed form, e.g. `ADCAbs %a, @counter` (`.txt` line ~1771).

So: absolute mode is an **address-matching transform**, and the single-use
load→ALU fold is a **separate isel pattern**. The two are independent wins.

## Current Irie path (what to change)

- `mem.load.i16` / `mem.store.i16` are legalized to per-byte
  `mem.load.byte_at @p, <off>` / `mem.store.byte_at` (offsets 0,1) by
  `MOS6502LegalizerInfo` (`MOS6502LegalizerInfo.cs:68-91`).
- `MOS6502InstructionSelector.SelectMemLoadByteAt` /
  `SelectMemStoreByteAt` (`MOS6502InstructionSelector.cs:774-953`) lower each
  byte to `lda.indy` / `sta.indy` through a zero-page pointer pair pinned to
  RC0/RC1, built by `EmitPointerSetup` / `EmitSymbolPointerBytes`
  (`lda.imm.symlo/symhi` → `sta.zp`). There is already a special case **above**
  this: addresses that resolve to a frame slot (`ResolveFrameSlotSymbol`) take a
  separate `mos6502.frame.load.byte` path. Genuine globals fall through to the
  indy path — that's the case to fix.
- `TryResolveSymbolAddress` (`:1166`) already resolves an address vreg defined by
  `mem.symbol @name` to its symbol name.
- The absolute opcodes **already exist** in `MOS6502Op` (`LdaAbs`, `StaAbs`,
  `AdcAbs`, `SbcAbs`, `CmpAbs`, `LdxAbs`, `StxAbs`, `LdyAbs`, `StyAbs`), but only
  `JmpAbs`/`JsrAbs` have `EmitOperandKind.AbsoluteAddress` emit rules today; the
  load/store/ALU abs forms have **no** `MOS6502MachineCodeEmitTable` rule (the
  table throws on unmapped opcodes).
- **Gap in the operand model:** `Symbol(string Name)` and
  `MachineCodeOperand.ExternalRef(string Name, SymbolHalf)` have **no offset**.
  Addressing `counter+1` (the i16 high byte) needs symbol+offset.

## Plan

### Stage A — absolute load/store for static symbols (the core win)

1. **Carry an offset on the symbol operand.**
   - `MirOperand.Symbol`: add `int Offset = 0` →
     `Symbol(string Name, int Offset = 0)` (`MirOperand.cs:16`). Update the MIR
     writer/parser to print/parse `@name+N` (optional; default 0 keeps existing
     output stable). Update `MirBinaryReader`/`MirBinaryWriter`.
   - `MachineCodeOperand.ExternalRef`: add `int Offset = 0`
     (`MachineCodeOperand.cs:20`).
   - `MOS6502BinaryEncoder.ResolveSymbol` / the `AbsoluteAddress` write site
     (`MOS6502BinaryEncoder.cs:171,235-236`): add `Offset` to the resolved
     address. Also update the assembly writer/parser to emit/accept `name+N`.

2. **Add emit rules** for the absolute load/store opcodes in
   `MOS6502MachineCodeEmitTable` with `EmitOperandKind.AbsoluteAddress` reading a
   `Symbol(name, offset)`:
   `LdaAbs`, `StaAbs`, `LdxAbs`, `StxAbs`, `LdyAbs`, `StyAbs` (and `AdcAbs`,
   `SbcAbs`, `CmpAbs` for Stage B). The `AbsoluteAddress` emitter must map a
   `Symbol(name, off)` → `ExternalRef(name, Full, off)`.

3. **Select absolute addressing for static symbols** in
   `SelectMemLoadByteAt` / `SelectMemStoreByteAt`. After the existing
   frame-slot branch, add a branch: if `TryResolveSymbolAddress(addrReg)` returns
   a (non-frame) symbol name, emit the absolute form instead of the indy path:
   - load: `%a:ac = mos6502.lda.abs Symbol(sym, off)` → funnel to an `Anyi8`
     vreg (same Ac→park idiom the indy path already uses).
   - store: `mos6502.sta.abs %val, Symbol(sym, off)`.
   Skip `EmitPointerSetup` / pointer-pair materialisation entirely for these.
   The indy path stays for genuine runtime i16 pointers (heap, computed
   addresses). `TryRemoveDeadMemSymbol` still cleans up the now-unused
   `mem.symbol`.

   This alone removes the 4-instruction pointer setup and the per-byte `LDY`,
   taking `global-rw` from 29 to ~13–15 instructions.

### Stage B — fold an absolute load into the ALU operand (optional, closes most of the rest)

Mirror llvm-mos `m_FoldedLdAbs`: when an `lda.abs @sym` result has a **single
use** that is the non-tied operand of `adc`/`sbc`/`cmp`, fold it into
`adc.abs @sym` / `sbc.abs` / `cmp.abs`, dropping the separate load. Cleanest as a
peephole in `MOS6502AddressingModeSelectorPass` (post-RA, already the place that
refines `Adc`→`AdcZp`/`AdcImm`): extend `RefineByOperand` so an operand that is
an absolute-symbol load folds to the `.abs` form. This brings `global-rw` to
parity-ish with llvm-mos's `adc counter` (~10–11).

Gate the fold on single-use (`GetUseCount` == 1) to avoid duplicating the load.

## Files to change

- `src/Irie/Mir/MirOperand.cs` — `Symbol` gains `Offset`.
- `src/Irie/Mir/Parsing/MirParser.cs`, `Mir/Writing/MirWriter.cs`,
  `Mir/Binary/MirBinary{Reader,Writer}.cs` — `@name+N` round-trip.
- `src/Irie/MachineCode/MachineCodeOperand.cs` — `ExternalRef` gains `Offset`.
- `src/Irie/Target/MOS6502/MOS6502BinaryEncoder.cs` — apply offset.
- `src/Irie/Target/MOS6502/MOS6502AssemblyWriter.cs` / `MOS6502AssemblyParser.cs`
  — `name+N` syntax.
- `src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs` — abs load/store/ALU
  emit rules.
- `src/Irie/Target/MOS6502/MOS6502InstructionSelector.cs` — absolute branch in
  `SelectMemLoadByteAt` / `SelectMemStoreByteAt`.
- `src/Irie/Target/MOS6502/MOS6502AddressingModeSelectorPass.cs` — Stage B fold.

## Verification

1. Add unit-level lit tests under `Lit/CodeGen/MOS6502/`:
   - an `--emit=asm` test showing `mos6502.lda.abs @counter` / `sta.abs` for a
     `mem.symbol` + `mem.load.i16` (Stage A);
   - a `--emit=bin … | irie-mc --hex-dump` test pinning the `name+1` offset
     resolves correctly (encoder).
2. Rewrite the `global-rw` reference test to the **faithful** C
   (`counter += delta`, arg-supplied) — now expressible end-to-end — and
   regenerate its golden CHECK block.
3. `dotnet test --project src/Irie.Tests` (Debug + Release) green; regenerate any
   other mem-access goldens that shift.
4. Re-run `irie-report`: confirm `global-rw` ratio drops from 2.9× toward ~1.0–1.3×
   and the headline aggregate improves. Remove the global-rw pairing caveat from
   `LlvmMosReference/README.md`.

## Risks / notes

- **Frame slots already special-cased** above the indy path — leave that branch
  first; only globals take the new absolute branch. Don't disturb the RC0/RC1
  pointer-pair logic the indy path still needs for runtime pointers.
- **i8 abs** (`mem.load.byte_at` offset 0 on an i8 global) gets absolute mode for
  free via the same branch.
- The `Symbol.Offset` change ripples through every `Symbol` construction site;
  defaulting `Offset = 0` keeps all existing call sites and golden outputs
  unchanged (verify the writer omits `+0`).
- Stage B is independent — ship Stage A first (the bulk of the win), measure with
  the report, then decide if the fold is worth it.
