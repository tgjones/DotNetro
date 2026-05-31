# MIR → MachineCode Plan

Bridge the gap between the unified MIR pipeline (`iriec`'s last pass,
`PseudoExpansionPass`) and the `MachineCode` layer. Today the two layers
exist as parallel structures with no code path between them: `iriec` ends
by printing post-expansion MIR text and stops. This plan fills in the
minimum needed to take the small subset of MIR opcodes that the current
pipeline actually produces all the way down to `MachineCodeInstruction`s
(and from there to assembly text or structured binary via the existing
`MOS6502AssemblyWriter` / `MachineCodeBinaryWriter`).

The plan is intentionally scoped to the **current** MIR subset. Anything
not produced by today's `MOS6502InstructionSelector` +
`MOS6502AddressingModeSelectorPass` + `MOS6502PseudoExpander` is
explicitly deferred. The emitter is built to fail loudly when it
encounters an unmapped opcode, so the gap is visible the moment isel
grows.

The plan is structured as:

1. Current state and what's missing
2. Design: hook on `Target`, emit table, emitter
3. Driver wiring
4. Tests
5. File-by-file delete / create / edit list
6. Stepwise landing order
7. Deferred work

---

## 1. Current state and what's missing

### Where the pipeline ends

`Irie.Tools.Compiler/Program.cs` builds the pass pipeline, runs it,
then calls `module.Write(Console.Out, target.GetRegisterName)`. The
output is MIR text; nothing consumes the post-`PseudoExpansionPass`
`MirModule` further.

### What the current subset produces

After all passes run on the IntegerAdd32 test
(`Lit/CodeGen/MOS6502/IntegerAdd32-PseudoExpansion.irie`), the MIR
contains exactly these `mos6502.*` opcodes:

| MIR opcode                        | Operand shape (post-AMS, post-expansion)             |
|-----------------------------------|------------------------------------------------------|
| `mos6502.clc`                     | `$c = mos6502.clc`                                    |
| `mos6502.adc.zp`                  | `$a, $c = mos6502.adc.zp $a, $zpN, $c`                |
| `mos6502.sta.zp`                  | `$zpN = mos6502.sta.zp $a`                            |
| `mos6502.lda.zp`                  | `$a = mos6502.lda.zp $zpN`                            |
| `mos6502.ldx.zp`                  | `$x = mos6502.ldx.zp $zpN`                            |
| `mos6502.txa`                     | `$a = mos6502.txa $x`                                 |
| `mos6502.rts`                     | `mos6502.rts implicit $a, implicit $x, …`             |

That's the entire surface area the first cut of the emitter needs to
cover.

### The gaps

1. **No MIR → MachineCode emitter.** There is no method on `Target`,
   no pass, no driver hook that produces a `MachineCodeModule` from a
   post-expansion `MirModule`.
2. **No `MOS6502Op` → `MOS6502Opcode` table.** The MIR side uses the
   enum (`MOS6502Op.AdcZp`); the MachineCode side uses the byte
   constants in `MOS6502Opcode` (`ADC_ZeroPage = 0x65`). Nothing maps
   between them.
3. **No per-opcode operand-shape rules.** A `PhysicalReg($zp4)` operand
   on `mos6502.adc.zp` must become `Immediate(4)` (the zero-page byte);
   the same physreg on `mos6502.txa` is implicit and does not appear in
   MachineCode at all. The rule varies by opcode and by which slot in
   the MIR operand array is being read.
4. **Implicit operands must be filtered.** `mos6502.rts implicit $a, …`
   has operands in MIR for liveness; `RTS` encodes no operands in
   MachineCode. Anything with `PhysicalReg.IsImplicit = true` is
   metadata for upstream passes only.
5. **Def-vs-use disambiguation.** `$zp4 = mos6502.sta.zp $a` lists
   `$zp4` as a def for liveness purposes, but the encoded operand is
   the address byte `4` (read from that def operand) — the source
   register `$a` is implicit in the opcode. The rule has to specify
   which operand position holds the address, not just "operand[0]".
6. **Block labels must be flattened.** MIR has named blocks (`bb0`,
   `bb1`); `MachineCode`'s flat stream needs a `MachineCodeLabel`
   entry before each block's instructions, and future branches will
   carry `MachineCodeOperand.LabelRef("bb1")`.
7. **No driver-level way to ask for MachineCode output.** Even with an
   emitter, `iriec` would still print MIR text. Either a new flag on
   `iriec` or a separate tool is needed.

---

## 2. Design

### 2.1 `Target.MachineCodeEmitter` hook

Add an explicit, non-pass hook to `Target`. The emitter produces a
different module type than the pipeline operates on, so it doesn't fit
the `MirFunctionPass` shape — making it a pass would force the pass
framework to grow a notion of "produces a different module," which
isn't justified by this single use site.

```csharp
// src/Irie/Target/Target.cs
public abstract class Target
{
    // … existing members …

    // Emits a MachineCodeModule from a post-PseudoExpansion MirModule.
    // Called by the driver after passMgr.Run(); not part of the pass pipeline.
    public abstract MachineCodeEmitter MachineCodeEmitter { get; }
}
```

```csharp
// src/Irie/Target/MachineCodeEmitter.cs
public abstract class MachineCodeEmitter
{
    public abstract Irie.MachineCode.MachineCodeModule Emit(Irie.Mir.MirModule module);
}
```

### 2.2 Emit table

The per-opcode rule lives in a new
`MOS6502MachineCodeEmitTable`. Each rule says (a) which 6502 byte to
emit, (b) which MIR operand position encodes into the instruction's
operand byte(s), and (c) how to interpret that operand.

```csharp
// src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs
public enum EmitOperandKind
{
    Implied,          // no operand byte
    ZeroPageAddress,  // 1-byte zp address from a PhysicalReg(zpN) operand
    Immediate,        // 1-byte immediate from an Immediate operand
    BranchTarget,     // 1-byte signed offset; emitter sees a BlockTarget,
                      // produces MachineCodeOperand.LabelRef
    AbsoluteAddress,  // 2-byte absolute address (future use: jsr/jmp)
}

public sealed record EmitRule(
    int             OpcodeByte,
    EmitOperandKind Kind,
    int?            OperandIndex);   // index into MIR Operands[] or null for Implied

public static class MOS6502MachineCodeEmitTable
{
    public static EmitRule Get(MOS6502Op op);
}
```

For the current subset:

| `MOS6502Op` | `OpcodeByte`        | `Kind`            | `OperandIndex` | Reasoning                                                        |
|-------------|---------------------|-------------------|----------------|------------------------------------------------------------------|
| `Clc`       | `CLC` = 0x18        | `Implied`         | —              | `$c = mos6502.clc` — def[0] is implicit-effect, not encoded.     |
| `AdcZp`     | `ADC_ZeroPage` 0x65 | `ZeroPageAddress` | 3              | Operands: def[0]=$a, def[1]=$c, use[0]=$a (tied), use[1]=zp ←, use[2]=$c. Index 3 = use[1]. |
| `StaZp`     | `STA_ZeroPage` 0x85 | `ZeroPageAddress` | 0              | Operands: def[0]=zp ←, use[0]=$a. Index 0 = def[0] (the address). |
| `LdaZp`     | `LDA_ZeroPage` 0xA5 | `ZeroPageAddress` | 1              | Operands: def[0]=$a, use[0]=zp ←. Index 1 = use[0].               |
| `LdxZp`     | `LDX_ZeroPage` 0xA6 | `ZeroPageAddress` | 1              | Same shape as `LdaZp`.                                            |
| `Txa`       | `TXA` = 0x8A        | `Implied`         | —              | Operands: def[0]=$a, use[0]=$x. Both implicit in encoding.        |
| `Rts`       | `RTS` = 0x60        | `Implied`         | —              | All operands are `implicit` flagged; trivially filtered.          |

`ZeroPageAddress` semantics: read the operand at the named index, assert
it is `PhysicalReg phys` with `phys.Id >= MOS6502Registers.RC(0)`, and
emit `MachineCodeOperand.Immediate(phys.Id - MOS6502Registers.RC(0))`.

### 2.3 `MOS6502MachineCodeEmitter`

```csharp
// src/Irie/Target/MOS6502/MOS6502MachineCodeEmitter.cs
public sealed class MOS6502MachineCodeEmitter : MachineCodeEmitter
{
    public override MachineCodeModule Emit(MirModule module)
    {
        var result = new MachineCodeModule();
        foreach (var function in module.Functions)
            EmitFunction(function, result.CreateFunction(function.Name));
        return result;
    }

    private static void EmitFunction(MirFunction src, MachineCodeFunction dst)
    {
        for (var blockIndex = 0; blockIndex < src.Blocks.Count; blockIndex++)
        {
            var block = src.Blocks[blockIndex];

            // Entry block needs no label; later blocks emit `.bbN:`.
            if (blockIndex > 0)
                dst.EmitLabel(BlockLabel(blockIndex));

            foreach (var instr in block.Instructions)
                EmitInstruction(instr, dst);
        }
    }

    private static void EmitInstruction(MirInstruction instr, MachineCodeFunction dst)
    {
        if (instr.Opcode.Dialect != MOS6502Dialect.Id)
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: cannot emit non-mos6502 opcode {instr.Opcode}.");

        var op = (MOS6502Op)instr.Opcode.Code;
        var rule = MOS6502MachineCodeEmitTable.Get(op);

        var operand = rule.Kind switch
        {
            EmitOperandKind.Implied         => (MachineCodeOperand?)null,
            EmitOperandKind.ZeroPageAddress => EmitZeroPage(instr, rule.OperandIndex!.Value),
            EmitOperandKind.Immediate       => EmitImmediate(instr, rule.OperandIndex!.Value),
            EmitOperandKind.BranchTarget    => EmitBranchTarget(instr, rule.OperandIndex!.Value),
            EmitOperandKind.AbsoluteAddress => EmitAbsoluteAddress(instr, rule.OperandIndex!.Value),
            _ => throw new InvalidOperationException($"Unhandled EmitOperandKind {rule.Kind}.")
        };

        dst.EmitInstruction(rule.OpcodeByte, operand == null ? [] : [operand]);
    }

    private static string BlockLabel(int blockIndex) => $"bb{blockIndex}";
    // ... EmitZeroPage / EmitImmediate / EmitBranchTarget / EmitAbsoluteAddress helpers ...
}
```

Notes on the helpers:

- `EmitZeroPage`: reads `instr.Operands[index]`, asserts
  `PhysicalReg phys && phys.Id >= MOS6502Registers.RC(0) && !phys.IsImplicit`,
  emits `Immediate(phys.Id - MOS6502Registers.RC(0))`.
- `EmitImmediate`: reads `instr.Operands[index]`, asserts `Immediate imm`,
  emits `MachineCodeOperand.Immediate(imm.Value)`.
- `EmitBranchTarget`: reads `BlockTarget target`, emits
  `MachineCodeOperand.LabelRef(BlockLabel(blockIndexOf(target.Block)))`.
  Not exercised by the current subset; included so the design is honest.
- `EmitAbsoluteAddress`: reads either a `Symbol` (external function name)
  or a `BlockTarget` (intra-function jump). Not exercised yet.

The emitter never touches operands whose role is implicit. It does not
inspect `PhysicalReg.IsImplicit` directly — the rule's `OperandIndex`
already skips them by construction.

### 2.4 Wiring on `MOS6502Target`

```csharp
// src/Irie/Target/MOS6502/MOS6502Target.cs
public override MachineCodeEmitter MachineCodeEmitter { get; } = new MOS6502MachineCodeEmitter();
```

---

## 3. Driver wiring

### 3.1 New `--emit` flag on `iriec`

```
iriec --target mos6502                     # emits MIR text (today's behaviour, default)
iriec --target mos6502 --emit=mir          # same as default
iriec --target mos6502 --emit=asm          # emits MOS6502 assembly text
iriec --target mos6502 --emit=mc           # emits MachineCode binary
```

Routing (after `passMgr.Run(context)`):

```csharp
switch (emit)
{
    case "mir":
        module.Write(Console.Out, target.GetRegisterName);
        break;
    case "asm":
    {
        var mc = target.MachineCodeEmitter.Emit(module);
        MOS6502AssemblyWriter.Write(mc, Console.Out);   // MOS6502-specific writer
        break;
    }
    case "mc":
    {
        var mc = target.MachineCodeEmitter.Emit(module);
        using var bw = new BinaryWriter(Console.OpenStandardOutput(),
                                        Encoding.UTF8, leaveOpen: true);
        mc.Write(bw);
        break;
    }
}
```

The MOS6502-specific call (`MOS6502AssemblyWriter.Write`) is fine in
`iriec`: `iriec` already knows the target by name (`--target mos6502`).
If a second target ever appears, that branch grows a `switch` on the
target name; we don't speculate now.

### 3.2 No new console app

`irie-mc` already handles MachineCode binary ↔ assembly text both ways.
Piping `iriec --emit=mc | irie-mc --disassemble` produces assembly text
without a new tool. The `--emit=asm` shortcut on `iriec` is convenience,
not necessity.

---

## 4. Tests

### 4.1 End-to-end lit test

New file `src/Irie.Tests/Lit/CodeGen/MOS6502/IntegerAdd32-MachineCode.irie`:

```
; RUN: @iriec --target mos6502 --emit=asm @file

; Final-output check: the same input as IntegerAdd32-PseudoExpansion.irie
; goes all the way to MOS6502 assembly text. Compares against the bytes
; we know each opcode lowers to; the zero-page operand addresses are the
; RC numbers chosen by the register allocator (zp2=$02, zp3=$03, ...,
; per MOS6502Registers.RC(n) = 12+n, with the printed address being n).

; CHECK: TestFunction:
; CHECK:     CLC
; CHECK:     ADC \$04
; CHECK:     STA \$04
; CHECK:     TXA
; CHECK:     ADC \$05
; CHECK:     STA \$05
; CHECK:     LDA \$02
; CHECK:     LDA \$06
; CHECK:     STA \$02
; CHECK:     ADC \$02
; CHECK:     STA \$02
; CHECK:     LDA \$03
; CHECK:     LDA \$07
; CHECK:     STA \$03
; CHECK:     ADC \$03
; CHECK:     STA \$03
; CHECK:     LDA \$04
; CHECK:     LDX \$05
; CHECK:     RTS

func @TestFunction : (i32, i32) -> i32 {
bb0(%0 : i32, %1 : i32):
    %2 : i32 = arith.addi %0, %1
    core.return %2
}
```

The expected byte-level outputs mirror the existing PseudoExpansion
CHECK lines, just rendered as `MOS6502AssemblyWriter` output. If the
register allocator's choices change, both this file and
`IntegerAdd32-PseudoExpansion.irie` move together.

### 4.2 Round-trip unit test

New unit test in `src/Irie.Tests/Mir/MachineCodeEmitterTests.cs` (or
similar) that:

1. Constructs a tiny `MirModule` programmatically (`mos6502.clc` +
   `mos6502.rts`), bypassing the parser.
2. Calls `new MOS6502Target().MachineCodeEmitter.Emit(module)`.
3. Asserts the resulting `MachineCodeModule` has the right opcode bytes
   and operand kinds.

This catches regressions in the table without needing the full pipeline.

### 4.3 Negative test — unmapped opcode

A unit test that constructs a MIR module containing one of the
pre-AMS opcodes the emitter doesn't yet support (e.g. `mos6502.cmp`),
calls `Emit`, and asserts a clear `KeyNotFoundException` /
`InvalidOperationException` naming the opcode. This is the safety net
that makes step-6 incremental opcode work risk-free: when isel grows
to emit a new opcode, this test class shows you where to teach the
emitter.

---

## 5. File-by-file delete / create / edit list

### Create

```
src/Irie/Target/MachineCodeEmitter.cs
src/Irie/Target/MOS6502/MOS6502MachineCodeEmitTable.cs
src/Irie/Target/MOS6502/MOS6502MachineCodeEmitter.cs
src/Irie.Tests/Lit/CodeGen/MOS6502/IntegerAdd32-MachineCode.irie
src/Irie.Tests/Mir/MachineCodeEmitterTests.cs
```

### Edit

```
src/Irie/Target/Target.cs
  + abstract MachineCodeEmitter MachineCodeEmitter { get; }

src/Irie/Target/MOS6502/MOS6502Target.cs
  + public override MachineCodeEmitter MachineCodeEmitter { get; } = new MOS6502MachineCodeEmitter();

src/Irie.Tools.Compiler/Program.cs
  + var emitOption = new Option<string>("--emit") { /* default "mir" */ };
  + Switch on emit after passMgr.Run; route to MIR text / MachineCode text / binary.
```

### Delete

None. The plan is purely additive — no existing behaviour changes.

---

## 6. Stepwise landing order

Each step compiles and tests pass before the next. Each step is one PR.

1. **Hook + abstract base**.
   Add `Target/MachineCodeEmitter.cs` and the abstract member on `Target`.
   `MOS6502Target` returns a stub that throws `NotImplementedException`.
   No behavioural change; the new abstract is unreferenced by the driver.

2. **`MOS6502MachineCodeEmitTable`**.
   Add the table file with the 7 entries above. Add the negative-test
   asserting `Get(MOS6502Op.Cmp)` throws (i.e. the table is intentionally
   incomplete).

3. **`MOS6502MachineCodeEmitter`**.
   Implement using the table. Wire on `MOS6502Target`. Add the round-trip
   unit test (step 4.2 from this plan).

4. **Driver flag**.
   Add `--emit` to `iriec`. Default `mir` → no change to existing tests.
   Add the end-to-end lit test (step 4.1).

5. **Verify against `irie-mc`**.
   Hand-test `iriec --emit=mc | irie-mc --disassemble` and check the
   output matches `--emit=asm`. No automated test for the pipe itself —
   `irie-mc` is already covered by its own RoundTrip.s test.

6. **Document**.
   Update [`CLAUDE.md`](../CLAUDE.md):
   - In the Irie bullet: add `MachineCodeEmitter` to the `Target/`
     description and add `MOS6502MachineCodeEmitter` /
     `MOS6502MachineCodeEmitTable` under `Target/MOS6502/`.
   - In the iriec tool description: mention the `--emit` flag.

---

## 7. Deferred work

Out of scope for this plan; each becomes a follow-up step keyed off a
new isel / AMS / expander opcode landing.

- **Branches**: `Beq`/`Bne`/`Bcc`/`Bcs`/`Bmi`/`Bpl`/`Bvc`/`Bvs` rules
  (`Relative` mode → `BranchTarget` kind). Block-label emission already
  works; just needs the rule rows and `EmitBranchTarget` impl.
- **Jumps / calls**: `JmpAbs`, `JsrAbs` (`AbsoluteAddress` kind).
- **Synthetic `Bgt`**: needs a `BgtExpansionPass` (lowers `bgt` → real
  branch pair) before the emitter sees anything. The emitter never
  needs a `Bgt` rule — by the time it runs, `Bgt` is gone.
- **Immediate addressing modes**: `LdaImm`, `LdxImm`, `LdyImm`, `AdcImm`,
  `CmpImm`, … (`Immediate` kind). Each adds one row.
- **Other addressing modes**: zp,X / zp,Y / abs / abs,X / abs,Y /
  (zp,X) / (zp),Y. Each addressing mode that isel/AMS ever emits gets a
  new `EmitOperandKind` value (or reuses an existing one if the
  encoding is the same shape) plus rule rows.
- **`InsertImplicitDef`-style operands** (e.g. `pseudo.copy` clobber
  metadata that survived to emission): not currently produced; if isel
  starts producing them, the rule for the affected opcode flags those
  positions as ignored.
- **Multi-function call emission**: needs symbol resolution between
  functions in the same module. `MachineCodeOperand.ExternalRef` is the
  carrier, but the call lowering / isel side has to emit a `Symbol`
  operand on the MIR `jsr.abs` for the emitter to translate.
- **AMS coverage**: `MOS6502AddressingModeSelectorPass` only refines
  `Adc` today. As it grows, the emitter table grows in parallel — the
  emitter never sees a pre-AMS opcode it can't refine, because the
  negative-test catches the pre-AMS opcode at the table-lookup site.
- **Linking / byte-level address resolution**: turning
  `MachineCodeOperand.LabelRef` / `ExternalRef` into actual bytes is
  the assembler's job (lives in `irie-mc` and downstream Sixty502
  integration), not the emitter's.

---

## Open questions to revisit during implementation

1. **Block-label naming**. Plan uses `bbN` where `N` is the block's
   index in `MirFunction.Blocks`. Alternative: use the printed MIR
   block name. They're identical today, but if MIR ever lets blocks be
   renamed, `MirBlock` would need a stable `Name` property. Defer.

2. **Per-target assembly writer dispatch in `iriec`**. The `--emit=asm`
   case hardcodes `MOS6502AssemblyWriter`. If a second target lands,
   either (a) move the writer onto `Target` as a virtual member, or
   (b) keep a switch in `iriec` keyed off `--target`. (a) is cleaner but
   premature now.

3. **Should the negative-test live in `Irie.Tests` or stay close to the
   table?** Plan puts it in the test project. If the table grows to
   dozens of opcodes, a unit test that enumerates `MOS6502Op` and
   asserts every value either has a rule or is explicitly opted-out
   (e.g. via an attribute or a deny-list) becomes more useful than a
   single hand-coded negative test. Defer until the table has ~20+
   entries.
