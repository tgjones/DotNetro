# MIR → Binary 6502 Plan

Today `iriec` can emit post-pipeline MIR text (`--emit=mir`), target
assembly text (`--emit=asm`), or the structured `MachineCode` binary
(`--emit=mc`). What's missing is the final step: **resolving a
`MachineCodeModule` into a flat byte stream of real 6502 opcode +
operand bytes** that a 6502 CPU can execute directly, plus optional
packaging of those bytes into target-system-specific image formats
(starting with BBC Micro `.ssd`).

This plan adds `--emit=bin` to `iriec` and introduces the concept of
**subtargets** so packaging is selected by `--target`. The two
subtargets in scope:

| `--target` value      | `--emit=bin` produces                          |
|-----------------------|------------------------------------------------|
| `mos6502`             | Raw 6502 machine code bytes (flat binary).     |
| `mos6502-bbcmicro`    | BBC Micro DFS disk image (`.ssd`) containing those same bytes as a single file. |

`--emit=asm` produces the **same** MOS6502 assembly text for both
subtargets — the BBC Micro variant only differs at the binary-packaging
step and in defaults for origin / load / exec addresses.

The plan is structured as:

1. Current state and what's missing
2. Design
   - 2.1 `MOS6502BinaryEncoder` (label/symbol resolution + byte emission)
   - 2.2 Subtarget hook on `Target` for image packaging
   - 2.3 BBC Micro DFS subtarget
   - 2.4 Driver wiring (`iriec`)
   - 2.5 Hex-dump helper for lit testability
3. Tests
4. File-by-file delete / create / edit list
5. Stepwise landing order
6. Deferred work

---

## 1. Current state and what's missing

### Where the MIR→bytes path ends today

```
MirModule
   │
   │  passMgr.Run(...)  →  Target.MachineCodeEmitter.Emit(...)
   ▼
MachineCodeModule       (label/instr stream; symbols + label refs unresolved)
   │
   ├── MOS6502AssemblyWriter ──► text (.asm via --emit=asm)
   ├── MachineCodeBinaryWriter ─► structured binary (--emit=mc)
   ╳   nothing                ──► flat 6502 bytes
```

`MachineCodeModule` already carries everything needed to encode bytes:

- `MachineCodeFunction.Name` — outer label.
- `MachineCodeLabel` entries — intra-function labels (`bb1:`, `.loop:`).
- `MachineCodeInstruction(Opcode, Operands[])` — opcode is the raw 6502
  byte (e.g. `0x65` for `ADC_ZeroPage`). Operands are one of:
  - `Register(int)` — never reaches this layer for MOS6502 (the
    MachineCodeEmitter already encodes register choices into the opcode
    byte, e.g. `LDA $zp` vs `LDX $zp` are distinct opcodes).
  - `Immediate(long)` — literal byte (`#$FF`) or literal 2-byte absolute
    address (`JSR $FFEE`).
  - `LabelRef(string)` — local label within the same function (resolved
    to absolute address for `JMP`/`JSR`, signed 8-bit offset for
    `Bxx`).
  - `ExternalRef(string, SymbolHalf)` — references another function in
    the module, or low/high half of one (`#<sym` / `#>sym`).

### The gaps

1. **No flat-byte encoder.** Nothing converts opcode + resolved operand
   into the 1/2/3 bytes a 6502 actually executes.
2. **No layout/symbol resolution.** Label and symbol references stay
   textual through the MachineCode layer; nothing assigns concrete
   addresses based on a load origin and instruction lengths.
3. **No notion of a subtarget / target system.** `MOS6502Target` knows
   about the chip but nothing about the machine the chip lives in
   (origin defaults, image format).
4. **No `iriec --emit=bin`.** Even with an encoder, the driver has no
   exit path that writes flat bytes.
5. **No way to package bytes into a `.ssd` disk image.** The legacy
   `DotNetCompiler` pipeline relies on Sixty502DotNet for this; the
   Irie pipeline has no equivalent.
6. **No lit-testable byte format.** Lit tests CHECK regex on stdout;
   raw bytes need a deterministic text rendering before they can be
   asserted on.

---

## 2. Design

### 2.1 `MOS6502BinaryEncoder`

A target-architecture-specific class that takes a `MachineCodeModule`
plus an origin and produces a `byte[]`. Two passes:

**Pass 1 — Layout.** Walk every `MachineCodeFunction` in declaration
order; walk every entry in each function's body:

- `MachineCodeLabel(name)` — record `addr → name` in a per-function
  label table (function name `func`, label name `lbl` → key
  `"func.lbl"` to keep namespaces flat).
- `MachineCodeInstruction(opcode, _)` — look up the addressing mode via
  `MOS6502InstructionInfo.Get(opcode).Mode`, compute the instruction
  length from the mode:
  - `Implied` (incl. `Accumulator`) → 1 byte
  - `Immediate`, `ZeroPage`, `ZeroPageX`, `ZeroPageY`, `IndirectX`,
    `IndirectY`, `Relative` → 2 bytes
  - `Absolute`, `AbsoluteX`, `AbsoluteY`, `Indirect` → 3 bytes

  Bump the layout cursor by the instruction length.
- At the start of each function record `addr → funcName` in a
  module-level symbol table.

After pass 1 the encoder has:
- `_functionAddrs : Dictionary<string, int>` — function-name → absolute
  address.
- `_localLabelAddrs : Dictionary<MachineCodeFunction, Dictionary<string, int>>` —
  per-function local labels.
- A list of `(int addr, MachineCodeFunction parent, MachineCodeInstruction instr)`
  for pass 2 to walk.

**Pass 2 — Encode.** For each `(addr, parent, instr)`:

1. Write `(byte)instr.Opcode` at `addr`.
2. Look at addressing mode:
   - `Implied` — done (1 byte).
   - `Immediate`, `ZeroPage`/`X`/`Y`, `IndirectX`/`Y` — read operand[0]:
     - `Immediate(v)` → write `(byte)(v & 0xFF)`.
     - `ExternalRef(name, SymbolHalf.LowByte)` → resolve to
       `_functionAddrs[name]`, write low byte.
     - `ExternalRef(name, SymbolHalf.HighByte)` → resolve, write high
       byte.
     - anything else → throw `InvalidOperationException` with the
       offending opcode/operand for diagnostics.
   - `Relative` — read operand[0] as `LabelRef(name)`:
     - resolve to `_localLabelAddrs[parent][name]`.
     - compute signed offset `target - (addr + 2)`.
     - if not in `[-128, 127]` throw — branch out of range.
     - write `(byte)offset`.
   - `Absolute`, `AbsoluteX`, `AbsoluteY`, `Indirect` — read operand[0]:
     - `Immediate(v)` → write `(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF)`
       (little-endian; covers `JSR $FFEE`).
     - `LabelRef(name)` → resolve via local-label table, write LE 16-bit.
     - `ExternalRef(name, SymbolHalf.Full)` → resolve via
       `_functionAddrs`, write LE 16-bit.
     - other halves on an absolute operand → throw (low/high halves
       only make sense in `Immediate` mode).

`ExternalRef` to a name not in `_functionAddrs` is a hard error for the
first cut. Linking external runtime symbols (BBC Micro OSWRCH etc.) is
already handled by passing them as `Immediate` literal addresses in MIR
— see `JsrAbsImmediate-MachineCode.irie`. Anything else is out of
scope.

Public surface:

```csharp
public sealed class MOS6502BinaryEncoder
{
    public byte[] Encode(MachineCodeModule module, int origin);
}
```

Layout-only output (for diagnostics / hex-dump prefix) is **not**
exposed on this class. If we want addresses in hex-dump output later,
we add a second method that returns the layout table alongside the
bytes.

### 2.2 Subtarget hook on `Target`

`Target` already exposes per-architecture hooks (CallLowering,
LegalizerInfo, etc.). We add two new abstractions for the system
("subtarget") dimension. Both default to mos6502 (raw chip) values, so
adding `MOS6502BbcMicroTarget` is a minimal override.

```csharp
// src/Irie/Target/Target.cs
public abstract class Target
{
    // … existing members …

    // Default origin (load address) when --origin is not supplied.
    // Null = the subtarget has no opinion; --origin is required for --emit=bin.
    public virtual int? DefaultOrigin => null;

    // Packages a flat 6502 byte buffer into a target-system image format.
    // Default: identity (raw bytes). Overridden by image-producing subtargets
    // such as MOS6502BbcMicroTarget.
    public virtual byte[] PackageImage(byte[] code, int origin) => code;
}
```

`MOS6502Target` does not override either (raw bytes, origin must be
specified by the user or callee).

### 2.3 BBC Micro DFS subtarget

```csharp
// src/Irie/Target/MOS6502/MOS6502BbcMicroTarget.cs
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000; // matches Aemula test harness

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(
            code,
            loadAddress: origin,
            execAddress: origin,
            fileName: "MAIN");   // hardcoded for the first cut
}
```

Everything except `DefaultOrigin` and `PackageImage` inherits from
`MOS6502Target` — same dialect, same call lowering, same instruction
selector, same emitter. That gives the user-requested property: same
`--emit=asm` output for both subtargets.

`BbcMicroDfsImage` is the new SSD packager:

```csharp
// src/Irie/Target/MOS6502/BbcMicroDfsImage.cs
internal static class BbcMicroDfsImage
{
    public static byte[] Build(byte[] code, int loadAddress, int execAddress, string fileName);
}
```

#### DFS image layout (acorn-dfs single-sided, single-density)

A `.ssd` file is a flat dump of disk sectors, each 256 bytes, 10
sectors per track. Minimum image for our needs: 2 catalog sectors +
enough data sectors to hold `code` rounded up to 256 bytes. We do
**not** zero-pad to a full 200KB disk — emulators are happy with a
short image and tests stay byte-counted.

| Offset    | Length      | Meaning                                              |
|-----------|-------------|------------------------------------------------------|
| `0x000`   | 8           | Disk title (chars 0–7), ASCII, space-padded          |
| `0x008`   | `8*n`       | File names — 7 chars + dir char (`$`) per file       |
| `0x100`   | 4           | Disk title (chars 8–11), boot option (`0`), cycle (`0`) |
| `0x104`   | 1           | `n*8` — number of file entries × 8                   |
| `0x105`   | 1           | Sector count high-byte bits + total-sectors high     |
| `0x106`   | 2           | Total sectors on the disc (LE 16-bit, after byte 5 high bits) |
| `0x108`   | `8*n`       | File entry — load(2), exec(2), len(2), high-bits(1), start-sector(1) |
| `0x200`   | `len(code)` | First file's bytes (single file = our `code`)        |

For a single-file DFS image of our code:

- Disk title = `"DotNetro"` (8 chars, no padding needed; bytes 8–11 = `0,0,0,0`).
- File name = `"MAIN   "` (7 chars, space-padded) + dir `"$"`.
- Load address = `loadAddress` (e.g. `0x2000`).
- Exec address = `execAddress` (same as load for our case).
- File length = `code.Length`.
- Start sector = `2` (catalogue takes sectors 0 and 1).
- High-bits byte at offset `0x10F` (per-file): bits `[7:6]`=exec[17:16],
  bits `[5:4]`=len[17:16], bits `[3:2]`=load[17:16], bits `[1:0]`=start-sector[9:8].
  For our small images these are all `0`.
- Total sectors (image size in sectors) = `2 + ceil(len / 256)`.

We trust the input: file names are forced to uppercase, truncated to 7
chars, and padded with spaces; non-printable / non-`[A-Z0-9_]` chars
throw. For the first cut, `fileName` is the constant `"MAIN"`.

### 2.4 Driver wiring (`iriec`)

`Irie.Tools.Compiler/Program.cs` changes:

1. **Target parsing.** Replace the inline switch:

   ```csharp
   Target target = targetName switch
   {
       "mos6502"          => new MOS6502Target(),
       "mos6502-bbcmicro" => new MOS6502BbcMicroTarget(),
       _ => throw new ArgumentException($"Unknown --target '{targetName}'."),
   };
   ```

2. **New options.** Add to the existing `Option<>` set:

   ```csharp
   var originOption = new Option<string?>("--origin")
   {
       Description = "Origin (load address) for --emit=bin, as hex (0x1900) or decimal. " +
                     "Defaults to the subtarget's default origin if specified."
   };
   ```

   Origin is parsed as `string?` so that `0x` prefixes work; we parse
   it with `Convert.ToInt32(s, fromBase: s.StartsWith("0x") ? 16 : 10)`.

3. **New `--emit=bin` arm** in the existing emit switch:

   ```csharp
   case "bin":
   {
       var mc = target.MachineCodeEmitter.Emit(module);
       var origin = ResolveOrigin(originOption, target.DefaultOrigin);
       var bytes  = new MOS6502BinaryEncoder().Encode(mc, origin);
       var image  = target.PackageImage(bytes, origin);
       using var stdout = Console.OpenStandardOutput();
       stdout.Write(image, 0, image.Length);
       break;
   }
   ```

   `ResolveOrigin` is a tiny local helper that returns the supplied
   value, or `target.DefaultOrigin`, or throws if neither is set.

4. **`--emit=asm` unchanged.** Both `MOS6502Target` and
   `MOS6502BbcMicroTarget` reach the same `MOS6502AssemblyWriter` path,
   producing identical output.

5. **Updated `--emit` help text.** New value `bin` documented.

### 2.5 Hex-dump helper for lit testability

Lit tests CHECK regex on stdout text. Raw bytes are binary, so we need
a deterministic text rendering of them for assertions. The user has
ruled out `--emit=hex` on `iriec`, so we put hex-dump on the existing
`irie-mc` tool as a third mode alongside `--assemble` / `--disassemble`:

```
irie-mc --hex-dump < bytes.bin
```

Output format — one row per 16 bytes, zero-based offset (not address),
space-separated upper-case hex:

```
00000000: A9 00 85 F0 A5 F0 F0 04  20 0A 19 4C 04 19 60 A9
00000010: 42 60
```

(Two-space gap between bytes 7 and 8 to match `xxd` convention so the
output is also legible if a developer eyeballs it.)

This is cross-platform (works on Windows cmd.exe because it's our own
.NET tool), and reusable for any binary input — including SSD images,
so a single lit test can verify both raw bytes and DFS bytes via the
same `| irie-mc --hex-dump` suffix.

CLI shape in `Irie.Tools.MachineCode/Program.cs`:

- Add `--hex-dump` bool option.
- Validate: exactly one of `--assemble`, `--disassemble`, `--hex-dump`.
- `--hex-dump` reads `input` (or stdin) as binary, writes formatted
  hex to `-o` (or stdout).

---

## 3. Tests

All new tests are lit tests under `src/Irie.Tests/Lit/CodeGen/MOS6502/`.
They live next to the existing `*-MachineCode.irie` tests and follow
the same naming convention with a `-Binary` or `-Ssd` suffix.

The tests use the new `iriec --emit=bin … | irie-mc --hex-dump`
pipeline so CHECK lines can regex on the hex output.

### 3.1 IntegerAdd32-Binary.irie

End-to-end: same input as `IntegerAdd32-MachineCode.irie`. Verifies that
each MIR opcode actually encodes to the documented 6502 byte:

```
; RUN: @iriec --target mos6502 --emit=bin --origin=0x1900 @file | @irie-mc --hex-dump

; CHECK: 00000000: 18 65 04 85 04 8A 65 05
; CHECK:           85 05 A5 02 A5 06 85 02
; CHECK: 00000010: 65 02 85 02 A5 03 A5 07
; CHECK:           85 03 65 03 85 03 A5 04
; CHECK: 00000020: A6 05 60

func @TestFunction : (i32, i32) -> i32 {
bb0(%0 : i32, %1 : i32):
    %2 : i32 = arith.addi %0, %1
    core.return %2
}
```

(Exact CHECK bytes will be regenerated from the encoder; the byte
sequence above is the expected lowering of the existing assembly-text
test for the same input. CHECK regex lets us split a single row across
multiple CHECK: lines for readability.)

### 3.2 Branch-Binary.irie

Smallest possible loop, to exercise:
- Per-function label table (`bb0`/`bb1` mapping).
- Relative-offset computation for `Bxx`.
- `JMP`/`JSR` absolute addressing.

```
; RUN: @iriec --target mos6502 --emit=bin --origin=0x1000 @file | @irie-mc --hex-dump

; CHECK: ... (CMP_Immediate F0, BEQ +N, JMP $1000, RTS at the right offsets)

func @Loop : () -> void {
bb0:
    mos6502.cmp.imm 0
    cf.cond_br beq, ^bb1, ^bb0
bb1:
    mos6502.rts
}
```

Branch-target focus: confirms `target - (instructionAddr + 2)` lands at
the right byte and is encoded as a signed 8-bit value.

### 3.3 CrossFunctionJsr-Binary.irie

Two functions in one module; the first one `JSR`s the second by name:

```
; RUN: @iriec --target mos6502 --emit=bin --origin=0x1000 @file | @irie-mc --hex-dump

; CHECK: ... (JSR <addr-of-helper> at offset 0, RTS at offset 3,
;             helper at offset 4)

func @Caller : () -> void {
bb0:
    mos6502.jsr.abs @helper
    mos6502.rts
}

func @helper : () -> void {
bb0:
    mos6502.rts
}
```

This verifies cross-function `ExternalRef` resolution. The hardcoded
expected bytes for the JSR operand must equal `origin + 4` little-endian.

### 3.4 JsrAbsImmediate-Binary.irie

Mirror of the existing `JsrAbsImmediate-MachineCode.irie` — confirms
that a literal `Immediate` address (e.g. OSWRCH at `$FFEE`) passes
through to the byte stream unchanged:

```
; CHECK: 00000000: 20 EE FF 60
```

### 3.5 SymbolHalfByte-Binary.irie

If `mos6502.lda.imm.symlo` / `.symhi` already appear in current
pipeline output anywhere, we add a tiny dedicated test that proves
`SymbolHalf.LowByte` / `.HighByte` resolve to the right byte of the
target symbol's address. Otherwise we defer this test and the
encoder's symbol-half branches stay covered only by the throw-on-bad-
shape path until the relevant isel pattern lands. (Search:
`grep -r "LdaImmSymLo\|LdaImmSymHi" src` — if the addressing-mode
selector can produce them today, we test them now; if not, this test is
deferred along with the legalizer pattern that would produce them.)

### 3.6 IntegerAdd32-Ssd.irie

End-to-end for the BBC Micro subtarget. Verifies the DFS image
structure: catalogue header, file entry, and code starting at offset
0x200.

```
; RUN: @iriec --target mos6502-bbcmicro --emit=bin @file | @irie-mc --hex-dump

; --- Sector 0: disk title + file names ---
; CHECK: 00000000: 44 6F 74 4E 65 74 72 6F  4D 41 49 4E 20 20 20 24
;                  D  o  t  N  e  t  r  o   M  A  I  N  _  _  _  $

; --- Sector 1: cycle, file entry ---
; CHECK: 00000100: 00 00 00 00 08 .. .. ..
;                  title-cont      n*8=08  …
; CHECK: 00000108: 00 20 00 20 23 00 00 02
;                  load=$2000 exec=$2000 len=$0023 high-bits=0 start-sec=2

; --- Sector 2: code ---
; CHECK: 00000200: 18 65 04 85 04 8A 65 05 …

func @TestFunction : (i32, i32) -> i32 {
bb0(%0 : i32, %1 : i32):
    %2 : i32 = arith.addi %0, %1
    core.return %2
}
```

(The `.. .. ..` placeholders cover bytes whose values aren't worth
pinning in the CHECK regex — chiefly the `total-sectors` field, which
depends on code length. We assert the structurally important fields:
filename, load/exec address, length, start sector, and the code at
sector 2.)

### 3.7 OutOfRangeBranch-Binary.irie (failure case)

A function whose branch target is more than 127 bytes away. Asserts the
encoder throws with a clear message:

```
; RUN: not @iriec --target mos6502 --emit=bin --origin=0x1000 @file
```

(The `RUN:` line uses lit's existing `not` prefix — see
`LitTestParser.cs:42` — which inverts the expected exit code.)

This pads the function body with NOPs to push the branch out of range.
Tests the diagnostic, not the surrounding pipeline.

### 3.8 UndefinedSymbol-Binary.irie (failure case)

`mos6502.jsr.abs @nonexistent` in a one-function module. Encoder must
throw with a message naming `nonexistent`. `RUN:` uses `not`.

---

## 4. File-by-file delete / create / edit list

### Create

- `src/Irie/Target/MOS6502/MOS6502BinaryEncoder.cs`
  — two-pass layout + encode (§2.1).
- `src/Irie/Target/MOS6502/MOS6502BbcMicroTarget.cs`
  — subtarget overriding `DefaultOrigin` and `PackageImage` (§2.3).
- `src/Irie/Target/MOS6502/BbcMicroDfsImage.cs`
  — DFS catalogue + file packager (§2.3).
- `src/Irie.Tests/Lit/CodeGen/MOS6502/IntegerAdd32-Binary.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/Branch-Binary.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/CrossFunctionJsr-Binary.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/JsrAbsImmediate-Binary.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/IntegerAdd32-Ssd.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/OutOfRangeBranch-Binary.irie`
- `src/Irie.Tests/Lit/CodeGen/MOS6502/UndefinedSymbol-Binary.irie`

### Edit

- `src/Irie/Target/Target.cs`
  — add `virtual int? DefaultOrigin` and `virtual byte[] PackageImage(...)`,
    both with sensible defaults (§2.2). Update the file-top comment to
    mention the subtarget hook.
- `src/Irie.Tools.Compiler/Program.cs`
  — add `--origin` option, add `mos6502-bbcmicro` to the target switch,
    add the `case "bin"` arm to the existing emit switch, update
    `--emit` description (§2.4).
- `src/Irie.Tools.MachineCode/Program.cs`
  — add `--hex-dump` option as a third mode; refactor the
    `assemble == disassemble` validation to allow three modes (§2.5).
- `CLAUDE.md`
  — extend the `Irie.Tools.Compiler` and `Irie.Tools.MachineCode` bullets
    in the "Key Projects" section to mention the new flags. Add the
    `mos6502-bbcmicro` subtarget under `Target/MOS6502/`.

### Delete

None. The MachineCode binary format and `irie-mc --assemble` /
`--disassemble` roundtrip continue to exist unchanged.

---

## 5. Stepwise landing order

Each step is a single PR-sized commit. Tests are added in the step that
needs them, not bundled at the end.

1. **Add `Target.DefaultOrigin` + `PackageImage` defaults.** Edit
   `Target.cs` only. No behaviour change yet (`MOS6502Target` doesn't
   override). Confirms the base class compiles before subclasses are
   added.
2. **Add `MOS6502BinaryEncoder` (layout only).** Implement pass 1
   exclusively; pass 2 throws `NotImplementedException`. Unit tests
   in `Irie.Tests/Target/` cover the layout table for a fixture
   `MachineCodeModule`. Not yet wired into `iriec`.
3. **Implement pass 2 of `MOS6502BinaryEncoder`.** Cover every
   `EmitOperandKind` case currently produced by the pipeline (§2.1).
   Unit tests for each addressing mode + the branch-offset arithmetic.
4. **Wire `--emit=bin` and `--origin` into `iriec`.** No subtarget yet;
   only `--target=mos6502`. Add `irie-mc --hex-dump`. Add lit tests
   3.1–3.4 and 3.7–3.8.
5. **Add `MOS6502BbcMicroTarget` + `BbcMicroDfsImage`.** Subtarget
   selectable via `--target=mos6502-bbcmicro`. Add lit test 3.6.
6. **Documentation pass.** Update `CLAUDE.md` to mention the new
   subtarget, the new emit value, and the `irie-mc --hex-dump` mode.

Step 1 unblocks step 2, step 2 unblocks step 3, etc. Step 6 is
independent of step 5 and could move earlier.

---

## 6. Deferred work

Not in scope for this plan; called out so the encoder's failure modes
are intentional, not accidental.

- **Linker-style external symbols.** Resolving `ExternalRef` to
  addresses outside the module (e.g. an OS routine by name rather
  than literal address). Today such calls go through `Immediate` in
  MIR; if isel grows a pattern that emits `ExternalRef("OSWRCH")`,
  a symbol map will need to be threaded through the encoder.
- **Long-branch lowering.** When a `Bxx` target lands more than ±127
  bytes away, the encoder throws. The fix is to substitute
  `Bxx_inverse +3 / JMP target`, but that's a peephole pass on the
  MachineCodeModule, not encoder business — better as a separate
  step driven by a real test case.
- **Data sections / `.byte` directives.** The encoder assumes every
  entry in `MachineCodeFunction.Body` is a `MachineCodeLabel` or a
  `MachineCodeInstruction`. Const pools / string tables will need a
  new entry variant before the encoder can lay them out.
- **Padding / alignment.** No alignment for entries. Add when a test
  case demands it.
- **Multiple BBC Micro files per disk image.** `BbcMicroDfsImage` is
  hardcoded to one file (`MAIN`) starting at sector 2. Multi-file
  images need a real catalogue layout with per-file sector
  computation — out of scope until the use case appears.
- **Other 6502 systems.** Commodore 64 (`.prg`), NES (`.nes`),
  Atari 2600 (`.bin`), etc. Each becomes a new
  `MOS6502<System>Target` subclassing `MOS6502Target`, overriding
  `DefaultOrigin` + `PackageImage`. No further core changes
  expected — that's the point of the subtarget hook.
- **Roundtrip of raw 6502 bytes back through the pipeline.** Explicitly
  out of scope per the request that motivated this plan.
