# Unified IR Plan

Replace `Irie.IR.*` and `Irie.CodeGen.*` with a single unified MIR — call it
`Irie.Mir` — that flows through all six pipeline stages in
[`notes/ir.txt`](ir.txt) without changing data shape. Only the opcode mix
and the level of physical-register / class detail evolve as passes run.

This plan assumes the answers given during planning:

| Decision      | Choice                                                                  |
|---------------|-------------------------------------------------------------------------|
| Scope         | Full rewrite including dialects and flags-as-physregs                   |
| Migration     | Replace in place (delete old IR + CodeGen, abandon `IR2/`)              |
| Reg naming    | `ir.txt` verbatim — `$a`, `$x`, `$y`, `$zp2`, `$c`, `$n`, `$v`, `$z`     |
| Flag regs     | First-class physical registers, not i1 vregs                            |
| Return op     | Two distinct ops: `core.return` (with value) → `pseudo.return` (post-ABI) → `mos6502.rts` (post-isel). Forward-only — `pseudo.return` never reverts to `core.return`. |
| Vreg form     | Type at def (`%0 : i8`) pre-isel; class at def (`%0 : ac`) post-isel    |
| Opcode shape  | Per-dialect enums; instruction carries `OpcodeRef { Dialect, Code }`     |

The plan is structured as:

1. Target data model
2. Dialect inventory
3. Text format (incl. round-trip rules)
4. Binary format
5. Pipeline (six passes, mapping from current passes)
6. Target-side responsibilities (MOS6502)
7. Tools (`irie-as`, `irie-dis`, `iriec`)
8. Test rewrites
9. File-by-file delete / create / edit list
10. Stepwise landing order

---

## 1. Target data model

```
Irie.Mir
├── MirModule              ; owns: list<MirFunction>
├── MirFunction            ; owns: name, signature, list<MirBlock>, vreg table
├── MirBlock               ; owns: parameters, liveins, list<MirInstruction>
├── MirInstruction         ; owns: opcode (OpcodeRef), operands (MirOperand[])
├── OpcodeRef              ; (DialectId, ushort Code) — value type
├── MirOperand             ; abstract; defs come first in the operand array
│   ├── VirtualReg         ; (int Id, bool IsDef)
│   ├── PhysicalReg        ; (int Id, bool IsDef, bool IsImplicit)
│   ├── Immediate          ; (long Value)
│   ├── BlockTarget        ; (MirBlock Block, MirOperand[] Args)
│   └── Symbol             ; (string Name)
└── VRegAnnotation         ; sum type stored per-vreg in MirFunction
    ├── TypedVReg          ; (IRType Type)             — pre-isel
    └── ClassedVReg        ; (int ClassId, string Name); set per-vreg by isel
```

### MirFunction

```csharp
public sealed class MirFunction(string name, IRType[] paramTypes, IRType returnType)
{
    public string Name { get; } = name;
    public IRType[] ParameterTypes { get; } = paramTypes;       // signature preserved
    public IRType   ReturnType    { get; } = returnType;

    public List<MirBlock> Blocks { get; } = [];

    // Virtual-register table. A vreg starts life as TypedVReg; isel may
    // overwrite the entry with ClassedVReg. There is never both at once.
    public int CreateVirtualRegister(IRType type);
    public int CreateVirtualRegisterInClass(int classId, string className);
    public VRegAnnotation GetVRegAnnotation(int vreg);
    public void ReclassifyVirtualRegister(int vreg, int classId, string className);

    // SSA helpers reused from MachineFunction today.
    public MirInstruction? GetDefinition(int vreg);
    public int GetUseCount(int vreg);
    public void ReplaceAllUsesOfRegister(int oldVreg, int newVreg);
    public bool IsTriviallyDead(MirInstruction instr);

    public void RebuildCfg();   // derives Predecessors/Successors from BlockTarget operands
}
```

The signature `(paramTypes, returnType)` lives on the function for **every
stage** — it is preserved by every pass, even after ABI lowering replaces
entry-block params with `pseudo.copy` from physregs. Writers always emit
`func @name : (T1, T2) -> T { ... }`; for void-return functions the writer
emits `func @name : (T1, T2) -> void { ... }`.

### MirBlock

```csharp
public sealed class MirBlock
{
    public List<int> Parameters { get; } = [];          // vreg IDs (typed/classed)
    public List<int> LiveIns    { get; } = [];          // physical register IDs

    public List<MirInstruction> Instructions { get; } = [];

    public List<MirBlock> Predecessors { get; } = [];   // populated by RebuildCfg
    public List<MirBlock> Successors   { get; } = [];

    public MirFunction? Parent { get; internal set; }
}
```

Notes:

- **Block parameters are typed/classed vregs**, not physregs. The entry block
  loses its parameters during ABI lowering (replaced with `pseudo.copy $a`
  etc.); non-entry blocks keep theirs until RA eliminates them. `LiveIns` is
  used for two purposes: (1) on the entry block, the ABI-mandated live-ins
  after ABI lowering; (2) on any block, the physregs known to be live on
  entry after RA (replaces today's per-edge `Liveness` info that today's
  MIR-text serialises post-RA).
- The set of operands on a terminator's `BlockTarget` for a successor must
  have arity equal to that successor's `Parameters.Count` (or zero after RA
  has run). Pass code validates this.

### MirInstruction

```csharp
public sealed class MirInstruction(OpcodeRef opcode, MirOperand[] operands)
{
    public OpcodeRef Opcode { get; } = opcode;
    public MirOperand[] Operands { get; internal set; } = operands;
    public MirBlock? Parent { get; internal set; }
}
```

Defs come first in `Operands`. Order within defs and uses is defined per
opcode by `InstructionInfo`.

### OpcodeRef + dialects

`DialectId` is **opaque** — it does not enumerate, name, or know about any
specific dialect. IDs are handed out by `DialectRegistry` at registration
time. Adding a new dialect does not touch `DialectId` or any central list.

```csharp
public readonly record struct DialectId(int Index);

public readonly record struct OpcodeRef(DialectId Dialect, ushort Code);
```

Each dialect is a self-contained class. It declares its own prefix string
and opcode enum, exposes a static `Id` slot that's populated when the
dialect registers, and offers a `Op(...)` helper for typed construction.

```csharp
public sealed class ArithDialect : Dialect
{
    public static DialectId Id { get; private set; }   // set by DialectRegistry.Register
    public override string Prefix => "arith";

    public enum Op : ushort { AddI, SubI, CmpI, AddICarry, /* ... */ }

    public static OpcodeRef OpRef(Op op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((Op)code) switch
    {
        Op.AddI       => "addi",
        Op.SubI       => "subi",
        Op.CmpI       => "cmpi",
        Op.AddICarry  => "addi_with_carry",
        _ => throw new ArgumentOutOfRangeException(),
    };

    // ... TryParseOp, GetInstructionInfo, IsSideEffectFree, etc.
}
```

`Dialect` is the abstract base:

```csharp
public abstract class Dialect
{
    public abstract string Prefix { get; }                     // "arith", "core", ...
    public abstract string GetOpName(ushort code);             // "addi", "cmpi", ...
    public abstract bool TryParseOp(string name, out ushort code);
    public abstract DialectInstructionInfo GetInstructionInfo(ushort code);
    public abstract bool IsSideEffectFree(ushort code);
    public abstract bool IsTerminator(ushort code);
    public abstract bool IsArtifact(ushort code);              // for legalizer

    // Called by the registry exactly once when the dialect is registered.
    // The dialect stores the assigned ID in its own static slot (or
    // instance field) — the registry doesn't need to know how.
    internal abstract void OnRegistered(DialectId id);
}

public static class DialectRegistry
{
    public static DialectId Register(Dialect dialect);          // returns the assigned ID
    public static Dialect ByPrefix(string prefix);              // for parser/writer
    public static Dialect ById(DialectId id);
}
```

Each dialect's `OnRegistered(id)` writes `id` into the dialect class's
`Id` slot. `Core`, `Arith`, `Cf`, `Pseudo` are registered at process
startup (e.g. from `Mir/Bootstrap.cs`); a `Target` registers its own
dialect from its constructor. Neither the registry nor `DialectId` ever
references a dialect class by name.

### MirOperand

```csharp
public abstract record MirOperand;

public sealed record VirtualReg(int Id, bool IsDefinition) : MirOperand;
public sealed record PhysicalReg(int Id, bool IsDefinition, bool IsImplicit = false) : MirOperand;
public sealed record Immediate(long Value) : MirOperand;
public sealed record BlockTarget(MirBlock Block, MirOperand[] Args) : MirOperand;
public sealed record Symbol(string Name) : MirOperand;
```

The `BlockTarget.Args` field carries successor block arguments (e.g.
`cf.cond_br %cond, bb1, bb2(%x)` → second target is
`BlockTarget(bb2, [VirtualReg(x, false)])`). After RA eliminates block
parameters, all `BlockTarget.Args` are empty arrays.

### Builder

`MirBuilder` mirrors today's `MachineIRBuilder` but talks in terms of
`OpcodeRef` and the new operand types. Same insertion-point design,
same observer hook (`IMirObserver` with `OnInstructionCreated` /
`OnInstructionErased`) used by the legalizer to keep its worklists
coherent.

---

## 2. Dialect inventory

### `core` dialect

| Opcode         | Operands                                | Notes |
|----------------|-----------------------------------------|-------|
| `core.return`  | `value: %v` (zero or one)               | Source-level return; carried until ABI lowering or RA. |

### `arith` dialect

All ops are pure SSA value producers. Result type matches operand types.

| Opcode                    | Operands (defs / uses)                       |
|---------------------------|----------------------------------------------|
| `arith.addi`              | `%r = arith.addi %a, %b`                     |
| `arith.subi`              | `%r = arith.subi %a, %b`                     |
| `arith.cmpi <pred>`       | `%cond = arith.cmpi sgt, %a, %b`             |
| `arith.addi_with_carry`   | `%r, %cout = arith.addi_with_carry %a, %b, %cin` |

`arith.cmpi` carries a predicate kind (`eq, ne, slt, sgt, sle, sge, ult, ugt, ule, uge`)
as an embedded `ImmediateOperand` or a side-channel field on a subclass
(implementation chooses one).

`arith.addi_with_carry` is the post-legalization narrowed-add form that
replaces today's `GenericAddCarry`. Its `cin`/`cout` uses **may be** vregs
(pre-isel) or `$c` (post-isel) — the operand is just `%v` or `$c`,
classified by isel.

### `cf` dialect

Terminator-only.

| Opcode          | Operands                                                |
|-----------------|---------------------------------------------------------|
| `cf.br`         | `cf.br bb1`        — single block-target               |
| `cf.cond_br`    | `cf.cond_br %cond, bb1, bb2(%x)` — i1 cond + 2 targets |

Block targets may carry arguments per `BlockTarget.Args`.

### `pseudo` dialect

All scaffolding ops. Lower in the pipeline they are expanded or eliminated.

| Opcode             | Shape                                                    |
|--------------------|----------------------------------------------------------|
| `pseudo.copy`      | `%d = pseudo.copy %s` or `$a = pseudo.copy %s` or any mix |
| `pseudo.merge`     | `%w = pseudo.merge %lo, %mid, …`                          |
| `pseudo.unmerge`   | `%lo, %hi = pseudo.unmerge %w`                            |
| `pseudo.return`    | `pseudo.return` — no operands; emitted by ABI lowering    |

Notes:

- `pseudo.copy` is the universal copy. Pre-isel it carries the source
  vreg's type. Isel can re-use it with a class on one side. RA reads
  copies to coalesce, eliminate, or expand them into target moves.
- `pseudo.merge` / `pseudo.unmerge` are the artifact ops folded by the
  legalization artifact combiner (today: `GenericMerge` / `GenericUnmerge`).
- `pseudo.return` is the void shell of a return after ABI lowering has
  moved the result into return physregs. **It survives all the way to
  instruction selection**, which lowers it to `mos6502.rts`. The
  transition is `core.return` (input) → `pseudo.return` (post-ABI) →
  `mos6502.rts` (post-isel). It never goes backwards.

### `mos6502` dialect

Target opcodes. Each post-isel opcode is the *addressing-mode-agnostic* form
(e.g. `mos6502.adc`); the addressing-mode-selection pass refines it to
`mos6502.adc.zp` etc. The final dialect surface is the union of all real 6502
mnemonic + addressing-mode pairs, plus a few synthetic ops (`mos6502.bgt`,
which expands later into `mos6502.bvc + mos6502.bne` or similar — exact form
is target's call).

Initial set needed to round-trip the ir.txt examples:

```
mos6502.clc                              ; $c = mos6502.clc
mos6502.adc      $a, %b, $c   → $a, $c   ; pre-AMS
mos6502.adc.zp   $a, %b, $c   → $a, $c   ; post-AMS
mos6502.cmp      $a, %b       → $n, $v, $z
mos6502.cmp.zp   $a, %b       → $n, $v, $z
mos6502.bgt      $n, $v, $z, bb1, bb2
mos6502.rts      [implicit $a, $x, ...]
mos6502.tax      $a           → $x
mos6502.tay      $a           → $y
mos6502.txa      $x           → $a
mos6502.tya      $y           → $a
mos6502.stx      $x           → $zpN
mos6502.lda.imm  imm          → $a       ; expansion target for some pseudo.copies
```

(Full list comes from `MOS6502InstructionInfo.cs` today; the rewrite
preserves all opcodes already present.)

---

## 3. Text format

Driven entirely by `ir.txt`. Round-trip layer is `MirParser` / `MirWriter`.

```
func @AddInt16 : (i16, i16) -> i16 {
bb0(%0 : i16, %1 : i16):
    %2 = arith.addi %0, %1
    core.return %2
}
```

Rules:

1. **Function header** is `func @<name> : (<paramType>, …) -> <returnType> {` —
   always. Stays identical through every pass.
2. **Block header** is `bb<N>(<param list>):` where each param is
   `%<id> : <annotation>`. Pre-isel `<annotation>` is a type
   (`i8`, `i16`, `i32`, `i1`). Post-isel it can be a class
   (`ac`, `xc`, `yc`, `zp`, `cc`, `vc`, `any8`).
3. **Liveins** appear on a separate line `[liveins: $a, $x]` immediately
   after the header, only when non-empty.
4. **Instructions** indented four spaces. Multi-def syntax:
   `%a : i8, %b : i1 = some.op …`. Defs printed with their annotations;
   uses unannotated (the annotation is taken from the def site).
5. **Physical registers** lowercase: `$a`, `$x`, `$y`, `$sp`, `$pc`, `$c`,
   `$n`, `$v`, `$z`, `$i`, `$d`, `$b`, `$zp0`..`$zpN`.
6. **Implicit operands** print as `implicit $r` / `implicit-def $r` —
   syntax preserved from today's `MachineWriter`.
7. **Block targets with args** print as `bb2(%x, %y)`; without args as `bb2`.
8. **Predicate kinds** for `arith.cmpi` print as a space-separated token
   immediately after the opcode: `%c = arith.cmpi sgt, %a, %b`.
9. **Class names** in vreg annotations are the lowercase names from the
   target's `RegisterInfo.GetRegisterClassName(id)`; pre-existing names get
   their `Imag8 → zp`, `Anyi8 → any8`, `Ac → ac`, `Xc → xc`, `Yc → yc`,
   `Cc → cc`, `Vc → vc` rewrite as part of the migration.
10. **Comments** are `;` to end-of-line. Parser ignores; writer never emits.
11. **No CHECK markers, no driver concerns** in the format itself.

### Parser two-pass model

Same as today's `MachineParser`: per-function, first pass scans block
headers (`bb<N>(` tokens) to pre-register each `MirBlock`; second pass
parses instructions, allowing forward block references in branches.

The lexer keeps the existing token vocabulary, augmented with:

- `Dot` (for dialect prefixes and `mos6502.adc.zp`)
- `PhysRegRef` (token text becomes the lower-cased name; matched against the
  target's register-name table)
- `ClassRef` (when used in a vreg annotation post-isel)
- `LBracket` / `RBracket` for `[liveins: …]`

### Writer responsibilities

- **Order** defs first then uses in printed operand list. Implicit operands
  trail the explicit ones.
- **Tied-def annotation**: today's writer prints `(tied-def N)` after a use
  that's tied to a def. Preserved by the new writer; pulled from
  `DialectInstructionInfo.TiedOperands`.

---

## 4. Binary format

Module-level structure:

```
[magic="IRI3"] [version=1]
[dialect_count] (per dialect: [prefix length+bytes] [id])
[function_count]
  per function:
    [name length+bytes]
    [param count] (per param: [type tag])
    [return type tag]
    [vreg count] (per vreg: [annotation kind] [annotation payload])
    [block count]
      per block:
        [param count] (per param: [vreg id])
        [livein count] (per livein: [physreg id])
        [instr count]
          per instr:
            [opcode { dialect id, code }]
            [operand count]
              per operand: [tag] [payload]
```

Operand tags mirror the `MirOperand` subtypes. `BlockTarget.Args` is encoded
as `(target index in function block list, arg count, args…)`. Vreg
annotation kinds: `0 = type`, `1 = class`.

Type tags continue today's enum (`Void, I1, I8, I16, I32`); extend with
`I1` (today's enum doesn't list it, but the IR already uses it).

`MirBinaryReader.Read(reader)` returns a fully-populated `MirModule`.
Dialect IDs are validated against the registry at read time; an unknown
dialect throws.

---

## 5. Pipeline

Mapping from today's passes to the new pipeline:

| Current pass                        | New pass                                    |
|-------------------------------------|---------------------------------------------|
| `IRTranslatorPass`                  | `AbiLoweringPass`                           |
| `LegalizerPass` + `LegalizationArtifactCombiner` | `LegalizerPass` (combiner stays as internal helper) |
| `InstructionSelectorPass`           | `InstructionSelectorPass`                   |
| `PhiEliminationPass`                | `PhiEliminationPass` (kept separate, pre-RA)|
| `TwoAddressInstructionPass`         | `TwoAddressInstructionPass` (kept separate, pre-RA) |
| `RegisterCoalescerPass`             | folded into `RegisterAllocatorPass`         |
| `RegisterAllocatorPass`             | `RegisterAllocatorPass`                     |
| `VirtualRegisterRewriterPass`       | folded into `RegisterAllocatorPass` (produces final physreg form) |
| `CopyEliminationPass`               | `CopyEliminationPass` (kept separate, post-RA) |
| —                                   | `MOS6502AddressingModeSelectorPass` (target-specific, added via target hook) |
| —                                   | `PseudoExpansionPass` (new)                 |

The final ordering in `iriec` is:

1. `AbiLoweringPass`
2. `LegalizerPass`
3. `InstructionSelectorPass`
4. `PhiEliminationPass`
5. `TwoAddressInstructionPass`
6. `RegisterAllocatorPass`
7. `CopyEliminationPass`
8. *Target-supplied post-RA passes* (for MOS6502: `MOS6502AddressingModeSelectorPass`)
9. `PseudoExpansionPass`

Steps 1–7 and 9 are generic and added unconditionally by `iriec`. Step 8 is
the target hook described in §6 (`Target.AddPostRegisterAllocationPasses`).

### 5.1 `AbiLoweringPass`

Inputs: source-form MIR with `arith.*`, `core.return`, `cf.*`,
typed-vreg block parameters.

Behaviour:

- For each function, drop entry-block parameters; emit at the start of the
  entry block:
  - Per arg byte: `AddLiveIn(physreg)`; `%vN : i8 = pseudo.copy $rN`.
  - If the arg type is wider than one byte: `%argVreg : iT =
    pseudo.merge %v0, %v1, …`.
  - Map the original block-arg vreg ID to the merged result for uses
    elsewhere in the function.
- For each `core.return %v`:
  - If `v` is wider than i8: insert `%lo, %hi, ... = pseudo.unmerge %v`.
  - Emit `$rN = pseudo.copy %byteN` for each return byte.
  - Replace the `core.return` with `pseudo.return`.
  - The signature on `MirFunction` is preserved (still `: (i16,i16) -> i16`).

Calling-convention details live in
`Target.CreateCallLowering()` — same shape as today's `MOS6502CallLowering`,
just emitting the new pseudo ops.

After this pass:
- Entry block has zero `Parameters`, populated `LiveIns`, and a prelude of
  `pseudo.copy` / `pseudo.merge` instructions.
- Every return path ends with `pseudo.unmerge` (optional), per-byte
  `pseudo.copy` to result regs, and `pseudo.return`.

### 5.2 `LegalizerPass`

Worklist-driven, same algorithm as today. Operates on `arith.*` ops whose
operand types are wider than the target's legal widths. Narrowing emits
`pseudo.unmerge` to split, per-byte `arith.addi_with_carry` chains, and
`pseudo.merge` to reassemble; an internal helper
(`LegalizationArtifactCombiner`, file-private to `LegalizerPass`) folds
matching merge/unmerge pairs that surround narrowed bodies. The combiner
keeps its own deduplicated worklist of artifact ops; the legalizer pumps
it between non-artifact drain cycles, exactly as today.

`LegalizerInfo` is target-specific. MOS6502 declares:
- `arith.addi i32 → NarrowScalar` (split into 4× `arith.addi_with_carry i8`)
- `arith.addi i16 → NarrowScalar` (2× chain)
- `arith.cmpi i32 → NarrowScalar` (TBD; not in ir.txt; mirror existing
  legalization story)

`pseudo.return` is **not** rewritten by legalization; it stays
`pseudo.return` until instruction selection lowers it to the target
return op (`mos6502.rts`).

### 5.3 `InstructionSelectorPass`

Maps `arith.*` operations to target opcodes. Driven by
`InstructionSelector.Select(MirInstruction)` per target.

For MOS6502:
- `arith.addi_with_carry` → `mos6502.clc` + `mos6502.adc` for the head of
  the chain; subsequent links use the previous `mos6502.adc`'s `$c` def.
  - First operand pre-coloured into class `ac` via `pseudo.copy`.
  - Second operand pre-coloured into class `zp` (or left as a vreg with
    class annotation `zp` — RA picks the actual zero-page slot).
  - Carry-in arg is `$c` post-isel (replacing the i1 vreg).
- `arith.cmpi sgt` + adjacent `cf.cond_br` → fuse into
  `mos6502.cmp` (defines `$n`, `$v`, `$z`) + `mos6502.bgt $n,$v,$z, …`.
- `pseudo.return` → `mos6502.rts`, surfacing the live-out return
  physregs (populated by ABI lowering's preceding `pseudo.copy`s) as
  `implicit $r` operands on the `mos6502.rts`.

Per-vreg, isel calls `ReclassifyVirtualRegister(vreg, classId, className)`
when it commits a vreg to a class. The vreg's TypedVReg entry is replaced
by ClassedVReg; the text output flips from `%5 : i8` to `%5 : zp`.

For operands constrained to a *specific* physreg (no class flexibility),
isel emits a `pseudo.copy` to that physreg and uses the physreg directly
in the target opcode — exactly as ir.txt shows.

### 5.4a `PhiEliminationPass` (separate, pre-RA)

For each terminator carrying `BlockTarget.Args`, emit per-edge
`pseudo.copy` from each arg to the successor's corresponding parameter
vreg, breaking cycles with a temp vreg in the standard way. After this
pass: every `BlockTarget.Args` is empty, and no `MirBlock.Parameters`
remain.

### 5.4b `TwoAddressInstructionPass` (separate, pre-RA)

For each instruction whose `DialectInstructionInfo` declares tied
def/use operands, emit a `pseudo.copy` to materialize the tied use as a
copy of the def-source so RA can pick a single physreg for both ends.
Same algorithm as today's pass, ported to MIR types.

### 5.4c `RegisterAllocatorPass`

Absorbs:

- **Coalescing**: prefer the register class hinted by surrounding
  `pseudo.copy` instructions (today's `RegisterCoalescerPass`).
- **Linear scan**: assign each vreg to a physical register within its
  class; today's `RegisterAllocatorPass` logic ported.
- **Vreg → physreg rewrite**: replace every `VirtualReg` operand with
  `PhysicalReg`; clear the vreg annotation table (today's
  `VirtualRegisterRewriterPass`).
- **Recompute liveins** for every block based on the final assignment;
  populate `MirBlock.LiveIns`.

After this pass: **no virtual registers** remain. Only `pseudo.copy`
(now phys→phys), target opcodes, and `mos6502.rts` remain. Identity and
trivially redundant copies are *not* cleaned up here — that's the next
pass.

### 5.4d `CopyEliminationPass` (separate, post-RA)

Drops identity `pseudo.copy $r → $r` and chases through trivially
redundant copy chains. Today's `CopyEliminationPass` ported to MIR
types. Operates on physreg-only MIR.

### 5.5 `MOS6502AddressingModeSelectorPass` (target-specific)

Not a generic pass — lives under `Target/MOS6502/` and is added to the
pipeline by `MOS6502Target.AddPostRegisterAllocationPasses(pm)` (the
generic hook described in §6). The pipeline doesn't have a stage called
"addressing mode selection"; it just calls
`target.AddPostRegisterAllocationPasses(pm)` between `CopyEliminationPass`
and `PseudoExpansionPass` and lets the target insert whatever it wants.

For each target opcode that exists in multiple addressing modes, the
MOS6502 implementation picks a mode based on the concrete physreg
operands:

- `mos6502.adc` with a `$zpN` second operand → `mos6502.adc.zp`.
- `mos6502.cmp` with a `$zpN` second operand → `mos6502.cmp.zp`.
- `mos6502.adc` with an `Immediate` second operand → `mos6502.adc.imm`.
- etc.

The pass is wholly target-internal; no shared interface or contract is
introduced at the generic layer. If a future target needs no addressing
mode work, it simply doesn't add any post-RA pass.

### 5.6 `PseudoExpansionPass`

Replace every remaining `pseudo.copy` with the appropriate target
move instruction. Target supplies a `PseudoExpander` that maps a
`(srcKind, dstKind, srcPhysReg, dstPhysReg)` tuple to one or more target
ops.

MOS6502 expansions:

- `$x = pseudo.copy $a` → `mos6502.tax $a → $x`
- `$y = pseudo.copy $a` → `mos6502.tay $a → $y`
- `$a = pseudo.copy $x` → `mos6502.txa $x → $a`
- `$a = pseudo.copy $y` → `mos6502.tya $y → $a`
- `$zpN = pseudo.copy $x` → `mos6502.stx $x → $zpN`
- `$zpN = pseudo.copy $y` → `mos6502.sty $y → $zpN`
- `$a = pseudo.copy $zpN` → `mos6502.lda.zp $zpN → $a`
- `$x = pseudo.copy $zpN` → `mos6502.ldx.zp $zpN → $x`
- `$y = pseudo.copy $zpN` → `mos6502.ldy.zp $zpN → $y`
- `$a = pseudo.copy <imm>` → `mos6502.lda.imm <imm> → $a` (after RA exposes
  `Immediate`-sourced copies, e.g. carry-in-zero materialization)
- impossible pairs (e.g. `$x = pseudo.copy $y`) → expand via `$a`.

This pass is the last; after it, the function contains only target opcodes
and `mos6502.rts`. No `pseudo.*` or `core.*` survive.

---

## 6. Target-side responsibilities

### `Target` abstract class

```csharp
public abstract class Target
{
    public abstract Dialect Dialect { get; }
    public abstract TargetInstructionInfo InstructionInfo { get; }
    public abstract TargetRegisterInfo    RegisterInfo    { get; }
    public abstract CallLowering          CallLowering    { get; }
    public abstract LegalizerInfo         LegalizerInfo   { get; }
    public abstract InstructionSelector   InstructionSelector { get; }
    public abstract PseudoExpander        PseudoExpander  { get; }

    // Called by iriec between CopyEliminationPass and PseudoExpansionPass.
    // The target appends any custom post-RA passes it wants. Default: no-op.
    public virtual void AddPostRegisterAllocationPasses(PassManager pm) { }
}
```

(`Target.Dialect` lets `DialectRegistry` discover the target dialect when
the target is registered. There is no generic
`AddressingModeSelector` contract — addressing mode selection is just one
of potentially many post-RA things a target might want to do, and it
lives wholly under `Target/MOS6502/`.)

### MOS6502 specifics

- `MOS6502Dialect` extends `Dialect`; opcode list and names sourced from
  `MOS6502InstructionInfo`.
- `MOS6502Op` enum carries every target opcode (pre-AMS and post-AMS forms,
  e.g. `Adc`, `AdcZp`, `AdcImm`, …).
- `MOS6502Registers` gains the flag regs as physreg IDs:
  - Existing: `A=0, X=1, Y=2, SP=3, PC=4, C=5`.
  - New: `N=6, V=7, Z=8, I=9, D=10, B=11`.
  - `RC(n)` shifts up by 6 to make room: `RC(n) = 12 + n`. Imaginary
    zero-page register prefix becomes `$zp` (display name), with
    `MOS6502RegisterInfo.GetRegisterName(RC(n))` returning `"zp" + n`.
- `MOS6502RegisterClass` renames its display names to lowercase
  (`Ac → ac`, `Xc → xc`, `Yc → yc`, `Cc → cc`, `Vc → vc`, `Imag8 → zp`,
  `Anyi8 → any8`). New classes if needed for `$n`/`$z` are added (e.g.
  `Nc`, `Zc`) — most of these single-register classes can be unified into
  one `flag` class if RA never has freedom there (TBD during
  implementation; the easiest first cut is one class per flag).
- `MOS6502CallLowering` rewritten to emit `pseudo.copy` and `pseudo.merge`
  in the new shape; signature handling unchanged.
- `MOS6502LegalizerInfo`: same NarrowScalar action shape; emits
  `arith.addi_with_carry` chains around the i32 → 4× i8 narrowing.
- `MOS6502InstructionSelector`: refactored to use the dialect-aware
  pattern dispatch (`OpcodeRef`-keyed switch); mostly mechanical.
- `MOS6502AddressingModeSelectorPass`: new file. Pattern-matches
  `mos6502.adc` / `mos6502.cmp` / `mos6502.lda` etc. against their operands
  and rewrites the opcode. Added to the pipeline by
  `MOS6502Target.AddPostRegisterAllocationPasses`.
- `MOS6502PseudoExpander`: new file. Implements the table above.
- `MOS6502AssemblyWriter` and `MOS6502AssemblyParser` (used by `irie-mc`)
  unchanged in their assembly-text concerns, but updated to map the new
  physreg IDs to mnemonic register references.

### Flag-register modelling

Flags become first-class physregs. Implications:

- An instruction that defines a flag (`mos6502.cmp` defines `$n,$v,$z`) lists
  those as physreg defs.
- A branch that reads flags (`mos6502.bgt`) lists them as physreg uses.
- `pseudo.copy` is **never** used for flags — flags are constrained to
  specific physregs by their producing/consuming opcodes; isel emits flag
  physregs directly.
- The current `Cc` class survives as the class of `$c` (since `$c` is the
  only valid carry storage); `Vc` survives for `$v`. Same for new
  single-register flag classes if needed.

---

## 7. Tools

### `irie-as`

Reads new text format from stdin or a file; writes new binary. Surface
unchanged (`Program.cs` keeps its `-o` and positional input argument).

### `irie-dis`

Reads new binary; writes new text. Surface unchanged.

### `iriec`

Reads new text MIR (only one format now); runs the pass pipeline; writes
text MIR back out.

The `--stop-after` / `--start-at` / `--run-pass` options keep working,
gated on the new pass names. The valid set is:

```
AbiLowering
Legalizer
InstructionSelector
PhiElimination
TwoAddressInstruction
RegisterAllocator
CopyElimination
MOS6502AddressingModeSelector    ; target-supplied
PseudoExpansion
```

The driver builds the pipeline as:

```csharp
var pm = new PassManager(stopAfterPass, startAtPass);
pm.AddPass(new AbiLoweringPass(target.CallLowering));
pm.AddPass(new LegalizerPass(target.LegalizerInfo));
pm.AddPass(new InstructionSelectorPass(target.InstructionSelector));
pm.AddPass(new PhiEliminationPass());
pm.AddPass(new TwoAddressInstructionPass(target.InstructionInfo));
pm.AddPass(new RegisterAllocatorPass(target.RegisterInfo, target.InstructionInfo));
pm.AddPass(new CopyEliminationPass());
target.AddPostRegisterAllocationPasses(pm);          // e.g. MOS6502AddressingModeSelectorPass
pm.AddPass(new PseudoExpansionPass(target.PseudoExpander));
pm.Run(context);
```

The `--input-language` option is **removed**. There's a single language
now; the driver always calls `MirModule.Parse`. The
`regAllocFlexibleI8Class` plumbing in today's `Program.cs` is also
removed — the register allocator gets the flexible class from
`Target.RegisterInfo.FlexibleI8ClassId` directly.

### `irie-mc`

`MachineCode/` subsystem is preserved. It reads and writes flat machine
code unrelated to the MIR refactor. Only touch point: the physical
register IDs used by `MachineCodeOperand.Register` must shift to the new
ID scheme (flag regs + RC offset change).

---

## 8. Test rewrites

All lit tests under `src/Irie.Tests/Lit/` are rewritten in the same
change. The rewrite is mechanical for the per-pass tests but somewhat
involved for the round-trip and integer-add ones.

### `Lit/RoundTrip.irie`

Rewrite to use the new opcode set + signature form. New content:

```
; RUN: @irie-as @file | @irie-dis

; CHECK: func @TestFunction : \(i32, i32\) -> i32 \{
func @TestFunction : (i32, i32) -> i32 {
; CHECK: bb0\(%0 : i32, %1 : i32\):
bb0(%0 : i32, %1 : i32):
  ; CHECK:     %2 : i32 = arith.addi %0, %1
  %2 : i32 = arith.addi %0, %1
  ; CHECK:     core.return %2
  core.return %2
}
```

(Question for implementation: do we annotate **every** def with its type
in the textual form, or only block-param defs? Plan: annotate every def
for parser simplicity. The annotation is redundant with the opcode's
return type but makes the format self-describing for tooling.)

### `Lit/CodeGen/MOS6502/IntegerAdd32-AbiLowering.irie` (renamed from `-IRTranslator`)

```
; RUN: @iriec --target mos6502 --stop-after AbiLowering @file

; CHECK: func @TestFunction : \(i32, i32\) -> i32 \{
; CHECK:   bb0\(\):
; CHECK:     liveins: \$a, \$x, \$zp2, \$zp3, \$zp4, \$zp5, \$zp6, \$zp7
; CHECK:     %0 : i8 = pseudo.copy \$a
; CHECK:     %1 : i8 = pseudo.copy \$x
; CHECK:     %2 : i8 = pseudo.copy \$zp2
; …
; CHECK:     %4 : i32 = pseudo.merge %0, %1, %2, %3
; …
; CHECK:     %10 : i32 = arith.addi %4, %9
; CHECK:     %11 : i8, %12 : i8, %13 : i8, %14 : i8 = pseudo.unmerge %10
; CHECK:     \$a = pseudo.copy %11
; …
; CHECK:     pseudo.return
; CHECK: \}
```

### `IntegerAdd32-Legalizer.irie`

CHECK lines updated to:

```
; CHECK:     %23 : i1 = arith.constant 0
; CHECK:     %24 : i8, %25 : i1 = arith.addi_with_carry %0, %5, %23
; …
; CHECK:     \$a = pseudo.copy %24
; …
; CHECK:     core.return
```

(After legalization, `pseudo.return` has been rewritten to `core.return`.)

### `IntegerAdd32-InstructionSelector.irie`

CHECK lines updated to:

```
; CHECK:     %0 : ac = pseudo.copy \$a
; …
; CHECK:     %5 : zp = pseudo.copy \$zp4
; …
; CHECK:     \$c = mos6502.clc
; CHECK:     %33 : ac, \$c = mos6502.adc %0(tied-def 0), %5, \$c
; …
; CHECK:     mos6502.rts implicit \$a, implicit \$x, implicit \$zp2, implicit \$zp3
```

### `IntegerAdd32-RegisterAllocator.irie`

Block parameters gone, all vregs replaced with physregs. Carry threaded
through `$c`. Coalesced copies eliminated.

### `IntegerAdd32-AddressingModeSelector.irie` (new)

CHECK that every `mos6502.adc` has been refined to `mos6502.adc.zp`.

### `IntegerAdd32-PseudoExpansion.irie` (new)

CHECK that every `pseudo.copy` has been replaced with the right move.

### Mir round-trip / PHI elimination / RegisterCoalescer tests

The current `RoundTrip.mir`, `PhiElimination-*.mir`,
`RegisterCoalescer-*.mir`, `CopyElimination-*.mir` files cover
behaviours that are folded into `RegisterAllocator` in the new pipeline.
Decision per migration step:

- `RoundTrip.mir` becomes a no-pass parse/print test of pre-RA MIR.
- `PhiElimination-*.mir` / `RegisterCoalescer-*.mir` / `CopyElimination-*.mir`
  become `RegisterAllocator-*.mir` lit tests, each crafted to exercise the
  same edge case the old test did (swap cycle, vreg→vreg coalescing,
  identity copy elimination, etc.). The CHECK lines change because the
  full pass runs end-to-end; the inputs are crafted so the only
  RA-internal phenomenon under test is the one each file covers.

### Unit tests (`IRModuleTests.cs`, `LivenessAnalysisTests.cs`, `RegisterAllocatorTests.cs`)

Rewritten against the new builder API. Same conceptual coverage; new types.

### DotLit / LitTests.cs

`LitTests.cs` doesn't change — it only iterates over `Lit/` files and runs
the RUN command. DotLit understands new `.irie` / `.mir` content because
it only parses the `;`-prefixed RUN/CHECK directives.

---

## 9. File-by-file delete / create / edit list

### Delete

```
src/Irie/IR/                       # entire tree (12 files)
  IRArgument.cs
  IRBasicBlock.cs
  IRBinaryOperatorInstruction.cs
  IRFunction.cs
  IRInstruction.cs
  IRIntegerLiteralInstruction.cs
  IRModule.cs
  IRReturnInstruction.cs
  IRType.cs
  IRUse.cs
  IRValue.cs
  IRWriter.cs
  TokenKind.cs
  Binary/IRBinaryFormat.cs
  Binary/IRBinaryReader.cs
  Binary/IRBinaryWriter.cs
  Parsing/Diagnostic.cs
  Parsing/IRLexer.cs
  Parsing/IRParser.cs
  Parsing/ParseException.cs
  Parsing/Token.cs

src/Irie/IR2/                      # placeholder, abandoned

src/Irie/CodeGen/                  # entire tree except possibly LivenessAnalysis
  CallLowering.cs                  → rewritten under Target/
  GenericOpcode.cs                 → replaced by per-dialect enums
  InstructionSelector.cs           → rewritten under Passes/
  LegalizerInfo.cs                 → rewritten under Target/
  MachineBasicBlock.cs             → replaced by Mir/MirBlock.cs
  MachineFunction.cs               → replaced by Mir/MirFunction.cs
  MachineIRBuilder.cs              → replaced by Mir/MirBuilder.cs
  MachineInstruction.cs            → replaced by Mir/MirInstruction.cs
  MachineModule.cs                 → replaced by Mir/MirModule.cs
  MachineOperand.cs                → replaced by Mir/MirOperand.cs
  MachineWriter.cs                 → replaced by Mir/Writing/MirWriter.cs
  TargetInstructionDescription.cs  → kept conceptually, renamed to DialectInstructionInfo
  TargetInstructionInfo.cs         → kept under Target/
  TargetRegisterInfo.cs            → kept under Target/
  TargetRegistry.cs                → kept; trivial port
  Target.cs                        → kept under Target/
  Analyses/Liveness.cs             → kept (move to Passes/Analyses/)
  Analyses/LivenessAnalysis.cs     → ported to new types
  Parsing/MachineLexer.cs          → replaced by Mir/Parsing/MirLexer.cs
  Parsing/MachineParser.cs         → replaced by Mir/Parsing/MirParser.cs
  Parsing/MachineToken.cs          → replaced
  Parsing/MachineTokenKind.cs      → replaced
  Passes/CopyEliminationPass.cs    → ported to new types (still separate)
  Passes/InstructionSelectorPass.cs → rewritten
  Passes/IRTranslatorPass.cs       → replaced by AbiLoweringPass
  Passes/LegalizationArtifactCombiner.cs → moved file-private into LegalizerPass
  Passes/LegalizerPass.cs          → ported
  Passes/PhiEliminationPass.cs     → ported to new types (still separate)
  Passes/RegisterAllocatorPass.cs  → rewritten, absorbs Coalescer + VRegRewriter only
  Passes/RegisterCoalescerPass.cs  → deleted (folded into RA)
  Passes/TwoAddressInstructionPass.cs → ported to new types (still separate)
  Passes/VirtualRegisterRewriterPass.cs → deleted (folded into RA)
```

### Create

```
src/Irie/Mir/
  MirModule.cs
  MirFunction.cs
  MirBlock.cs
  MirInstruction.cs
  MirOperand.cs
  OpcodeRef.cs
  Dialect.cs
  DialectRegistry.cs
  DialectInstructionInfo.cs
  VRegAnnotation.cs
  MirBuilder.cs
  IMirObserver.cs
  Parsing/MirParser.cs
  Parsing/MirLexer.cs
  Parsing/MirToken.cs
  Parsing/MirTokenKind.cs
  Parsing/ParseException.cs
  Parsing/Diagnostic.cs
  Writing/MirWriter.cs
  Binary/MirBinaryFormat.cs
  Binary/MirBinaryReader.cs
  Binary/MirBinaryWriter.cs
src/Irie/Dialects/
  Core/CoreDialect.cs
  Core/CoreOp.cs
  Arith/ArithDialect.cs
  Arith/ArithOp.cs
  Arith/ArithCmpPredicate.cs
  Cf/CfDialect.cs
  Cf/CfOp.cs
  Pseudo/PseudoDialect.cs
  Pseudo/PseudoOp.cs
src/Irie/Passes/
  Pass.cs                          (moved from Irie/Pass.cs)
  PassManager.cs
  CompilationContext.cs
  MirFunctionPass.cs
  MirFunctionAnalysis.cs
  Analyses/Liveness.cs
  Analyses/LivenessAnalysis.cs
  AbiLoweringPass.cs
  LegalizerPass.cs                 (contains private LegalizationArtifactCombiner)
  InstructionSelectorPass.cs
  PhiEliminationPass.cs
  TwoAddressInstructionPass.cs
  RegisterAllocatorPass.cs
  CopyEliminationPass.cs
  PseudoExpansionPass.cs
src/Irie/Target/
  Target.cs
  TargetRegistry.cs
  TargetRegisterInfo.cs
  TargetInstructionInfo.cs
  CallLowering.cs
  LegalizerInfo.cs
  InstructionSelector.cs
  PseudoExpander.cs
src/Irie/Target/MOS6502/
  MOS6502Dialect.cs                                (new)
  MOS6502Op.cs                                     (new — enum of all opcodes)
  MOS6502Target.cs                                 (rewritten)
  MOS6502Registers.cs                              (edited: add flag regs, shift RC)
  MOS6502RegisterClass.cs                          (edited: lowercase names)
  MOS6502RegisterInfo.cs                           (edited: new IDs/names)
  MOS6502InstructionInfo.cs                        (rewritten to dialect-aware shape)
  MOS6502CallLowering.cs                           (rewritten: emits new pseudos)
  MOS6502LegalizerInfo.cs                          (rewritten)
  MOS6502InstructionSelector.cs                    (rewritten)
  MOS6502AddressingModeSelectorPass.cs             (new, target-private MirFunctionPass)
  MOS6502PseudoExpander.cs                         (new)
  MOS6502AssemblyWriter.cs                         (edited: physreg IDs)
  MOS6502AssemblyParser.cs                         (edited: physreg IDs)
```

### Edit

```
src/Irie.Tools.Compiler/Program.cs
  - Replace IRModule.Parse / MachineModule.Parse with MirModule.Parse.
  - Replace pass list with the new pipeline (see §7).
  - Remove --input-language option entirely (single language now).
  - Drop regAllocFlexibleI8Class plumbing.
  - Call target.AddPostRegisterAllocationPasses(pm) between CopyElim
    and PseudoExpansion.

src/Irie.Tools.Assembler/Program.cs
  - IRModule → MirModule.

src/Irie.Tools.Disassembler/Program.cs
  - IRModule.Read → MirModule.Read.

src/Irie.Tools.MachineCode/Program.cs
  - Touch only if physreg IDs leak into the machine-code layer
    (they do via MachineCodeOperand.Register; verify and adjust).

src/Irie.Tests/Lit/*.irie / *.mir
  - All files rewritten (or replaced) — see §8.

src/Irie.Tests/IR/IRModuleTests.cs
  - Renamed to Mir/MirModuleTests.cs; rewritten against MirBuilder API.

src/Irie.Tests/CodeGen/LivenessAnalysisTests.cs
src/Irie.Tests/CodeGen/RegisterAllocatorTests.cs
  - Renamed under Passes/; rewritten against MirFunction API.
```

---

## 10. Stepwise landing order

Each step compiles and tests pass before moving on. Each step is one PR /
one commit.

1. **Mir core types** (no behaviour).
   Add `Irie/Mir/{MirModule,MirFunction,MirBlock,MirInstruction,
   MirOperand,OpcodeRef,VRegAnnotation,Dialect,DialectRegistry,
   DialectInstructionInfo,MirBuilder,IMirObserver}.cs`. Add the four
   non-target dialects (`Core`, `Arith`, `Cf`, `Pseudo`) with empty
   `DialectInstructionInfo` placeholders. Compile only; no callers.

2. **Mir text format**.
   Add `Mir/Parsing/MirLexer.cs`, `MirParser.cs`, `MirToken*.cs`,
   `Mir/Writing/MirWriter.cs`. Add a focused unit test that parses and
   round-trips a hand-written sample for each dialect. Lit tests not
   touched yet.

3. **Mir binary format**.
   Add `Mir/Binary/MirBinaryFormat.cs`, `MirBinaryReader.cs`,
   `MirBinaryWriter.cs`. Add unit tests that round-trip a sample.

4. **MOS6502 dialect + target rewrite (compile-time only)**.
   Add `MOS6502Dialect`, `MOS6502Op`, edited `MOS6502Registers` and
   `MOS6502RegisterClass`. Rewrite `MOS6502InstructionInfo`,
   `MOS6502RegisterInfo`. **Old `CodeGen/` still exists** but the new
   target lives alongside under `Target/MOS6502/` paths. (Temporarily,
   the new target is unwired.)

5. **New pass plumbing**.
   Add `Passes/{Pass,PassManager,CompilationContext,MirFunctionPass,
   MirFunctionAnalysis,Analyses/Liveness,Analyses/LivenessAnalysis}.cs`.
   These reference Mir types only. Old passes still exist for now.

6. **`AbiLoweringPass` + new `MOS6502CallLowering`**.
   Implement the pass and target hook. Add lit test
   `IntegerAdd32-AbiLowering.irie`. **Don't delete old passes yet** —
   keep both pipelines alive in `iriec` behind a `--engine=v2` flag
   while iterating.

7. **`LegalizerPass` + `LegalizationArtifactCombiner` + `MOS6502LegalizerInfo`**.
   Port to new types. Add lit test `IntegerAdd32-Legalizer.irie` in new
   format.

8. **`InstructionSelectorPass` + `MOS6502InstructionSelector`**.
   Port. Add lit test in new format.

9. **`PhiEliminationPass`** (separate, pre-RA).
   Port today's pass to MIR types. Lit test for the swap-cycle edge case.

10. **`TwoAddressInstructionPass`** (separate, pre-RA).
    Port today's pass to MIR types. Lit test.

11. **`RegisterAllocatorPass`** (absorbs coalescer + vreg-rewriter only).
    Port today's RA logic to MIR types, with trivial coalescing
    integrated. Final state: a single pass that consumes post-isel MIR
    with no block params (PhiElim ran) and produces post-RA MIR with no
    vregs.

12. **`CopyEliminationPass`** (separate, post-RA).
    Port today's pass to MIR types. Lit test for identity-copy and
    chain-copy cases.

13. **`MOS6502AddressingModeSelectorPass`**.
    New target-private pass. Lit test.

14. **`PseudoExpansionPass` + `MOS6502PseudoExpander`**.
    New pass. Lit test.

15. **Cut over `iriec`**.
    Replace the old pass pipeline with the new list. Remove the
    `--engine=v2` flag from step 6. Remove `--input-language`.

16. **Cut over `irie-as` / `irie-dis`**.
    Switch to `MirModule.Parse` / `Read` / `Write`.

17. **Delete old IR + CodeGen trees**.
    `src/Irie/IR/`, `src/Irie/IR2/`, `src/Irie/CodeGen/` deleted. All
    references resolved.

18. **Rewrite remaining lit tests + unit tests**.
    Sweep through `src/Irie.Tests/`; resolve any test that still
    references the old API.

19. **Update CLAUDE.md** to describe the unified IR (replace the existing
    "Irie Layer Roadmap" three-layer description with a single-layer one)
    and remove `notes/ir.txt` reference if obsolete.

---

## Open questions to revisit during implementation

1. **`arith.cmpi` predicate encoding** — embedded immediate vs. opcode
   subkind. Choose during step 7 / 8 work.
2. **Single `flag` class vs per-flag class** for $n/$v/$z — choose during
   step 4 once `MOS6502RegisterClass` is finalised.
3. **Predicate fusion** — does the instruction selector fuse
   `arith.cmpi` + `cf.cond_br` into one pair of target ops, or first
   select them independently and then fold? Plan: independent selection
   with a small peephole at the end of isel. Revisit if peephole gets
   complicated.
4. **Pre-RA terminator block-arg representation** — `BlockTarget.Args` as
   shown above, or split args out of the block target into a separate
   side-list? Plan: keep them on `BlockTarget` for textual locality;
   easy to refactor if needed.
5. *Resolved.* `core.return` → `pseudo.return` (in ABI lowering) →
   `mos6502.rts` (in isel). `PseudoExpansionPass` handles only
   `pseudo.copy` and friends; the return chain is forward-only.
