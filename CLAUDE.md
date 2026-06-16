# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Is

DotNetro is a .NET AOT compiler that transpiles .NET assemblies (IL bytecode) to 6502 assembly code, targeting retro computers like the BBC Micro. It reads ECMA-335 metadata, walks IL instructions, and emits 6502 mnemonics which are then assembled into BBC Micro disk images (`.ssd`).

## Build & Test Commands

```bash
# Build and run all tests
dotnet test --solution src

# Run tests for a specific project
dotnet test --project src/DotNetro.Compiler.Tests
dotnet test --project src/Irie.Tests
dotnet test --project src/DotLit.Tests

# Filtering: TUnit uses --treenode-filter (NOT --filter, which silently runs zero tests).
# The path is /Assembly/Namespace/Class/Method — escape literal '.' in the displayed
# name as itself; the renderer shows '·' but you type '.'.

# Run all tests in a unit-test class
dotnet test --project src/Irie.Tests --treenode-filter "/*/*/RegisterAllocatorTests/*"

# Run a single unit-test method by name
dotnet test --project src/Irie.Tests --treenode-filter "/*/*/*/AAndXLiveAcrossCopy_PicksY"
```

**You cannot target an individual lit test with --treenode-filter.** Lit tests are
parameterized as `LitTest(Lit/CodeGen/.../Foo·irie)`, and the path argument contains
`/` (parsed as tree-node separators) and `()` (parsed as grouping operators), so any
attempt to narrow by filename either matches all 97 lit tests or zero. Don't fight it —
just run the whole project. Both lit suites are fast (Irie.Tests ~3–5s,
DotNetro.Compiler.Tests ~12s):

```bash
dotnet test --project src/Irie.Tests              # all Irie tests incl. every .irie/.s lit test
dotnet test --project src/DotNetro.Compiler.Tests # all compiler tests incl. every .cs lit test
```

CI runs `dotnet test --solution src` in both Debug and Release configurations.

## Architecture

### Compilation Pipeline

```
.NET Assembly (.dll)
    → DotNetCompiler  (reads ECMA-335 metadata, walks IL via ILReader, emits 6502 asm string)
    → CompilerDriver  (hands asm string to Sixty502 assembler)
    → Output: .asm (assembly source), .lst (listing), .ssd (BBC Micro disk image)
```

### Key Projects

- **DotNetro.Compiler** — core library; almost all logic lives here
  - `DotNetCompiler.cs` — large (~10K lines) orchestrator; contains the IL→codegen switch statement in `CompileMethodBody()`
  - `CompilerDriver.cs` — coordinates compilation and assembly steps
  - `CodeGen/M6502CodeGenerator.cs` — ~22K lines; one method per IL opcode emitting 6502 mnemonics
  - `CodeGen/BbcMicroCodeGenerator.cs` — BBC Micro specialization (MODE 7 setup, OS calls)
  - `TypeSystem/TypeSystemContext.cs` — type resolution cache; types are lazily resolved (EnsureFields, EnsureBaseType pattern)
  - `EcmaAssembly/EcmaType/EcmaMethod/EcmaField` — wrappers over System.Reflection.Metadata

- **DotNetro.Compiler.Driver** — console app (`dnrc`); CLI flags `--assembly` and `--output`

- **DotNetro** — minimal runtime library that gets compiled into output; provides `ConsoleHelper` and `ManagedHeap`

- **Irie** — unified MIR + MachineCode scaffolding; not yet wired into the main compiler
  - `Mir/` — single unified MIR layer; one IR flows through every pass, with only the opcode mix and the level of physreg / class detail evolving as passes run; `;` starts a line comment
    - `MirModule` / `MirFunction` / `MirBlock` / `MirInstruction` — structural containers. `MirFunction` preserves its `(paramTypes) -> returnType` signature across every pass (printed as `func @name : (i16, i16) -> i16 { … }`). `MirBlock.Parameters` is a list of vreg IDs (block arguments used in place of PHI nodes); `MirBlock.LiveIns` is a list of physical register IDs (calling-convention live-ins after ABI lowering; final live-ins after RA).
    - `MirOperand` — abstract record; variants: `VirtualReg(int Id, bool IsDefinition)`, `PhysicalReg(int Id, bool IsDefinition, bool IsImplicit)`, `Immediate(long)`, `BlockTarget(MirBlock, MirOperand[] Args)` (successor + optional block arguments), `Symbol(string)`. Defs come first in the operand array.
    - `OpcodeRef(DialectId, ushort Code)` — every instruction's opcode is a `(dialect, code)` pair. `DialectId` is opaque — dialects are looked up via `DialectRegistry.ByPrefix` / `.ById`.
    - `Dialect` — abstract base bundling a prefix string, an opcode enum, name parsing, per-opcode `DialectInstructionInfo` (operand classes, tied operands, implicit defs/uses), and the `IsSideEffectFree` / `IsTerminator` / `IsArtifact` flags used by passes. `MirBootstrap.EnsureRegistered()` registers the four non-target dialects; targets register their own from their constructor.
    - `VRegAnnotation` — per-vreg annotation stored on `MirFunction`. A vreg starts as `TypedVReg(IRType)` pre-isel and is overwritten with `ClassedVReg(int ClassId, string Name)` by `MirFunction.ReclassifyVirtualRegister` during instruction selection. `ClearVRegAnnotations` is called by RA after every `VirtualReg` operand has been replaced with a `PhysicalReg`.
    - `MirFunction` def/use helpers (`GetDefinition`, `GetUseCount`, `ReplaceAllUsesOfRegister`, `IsTriviallyDead`, `RebuildCfg`) — same SSA tools the old `MachineFunction` had, ported to MIR types.
    - `MirBuilder` — insertion-point-based builder; supports an `IMirObserver` hook (`OnInstructionCreated` / `OnInstructionErased`) so passes can be notified of builder-driven mutations (used by the legalizer to keep its worklists coherent).
    - `Mir/Parsing/MirParser` — text parser; two-pass within each function (scan block headers for `bbN(` first, then parse instructions) so branches can forward-reference blocks. Supports multi-def syntax `%a : i8, %b : i1 = some.op …`. `Mir/Writing/MirWriter` prints with `func @name : (…) -> T` header, vregs annotated at their def site (type pre-isel, class post-isel), and `[liveins: $a, $x]` lines on blocks with non-empty live-ins.
    - `Mir/Binary/MirBinaryReader` / `MirBinaryWriter` — structured binary format; dialects encoded by prefix+id and validated against the registry on read.
  - `Dialects/` — opcode definitions used in the MIR. Each dialect is one folder.
    - `Core` — `core.return` (source-level return, carried until ABI lowering).
    - `Arith` — pure SSA value producers: `arith.addi`, `arith.subi`, `arith.cmpi <pred>` (predicate from `ArithCmpPredicate`), `arith.addi_with_carry` (2 defs: result + carry_out; 3 uses: a, b, carry_in — the post-legalization narrow form of multi-byte add).
    - `Cf` — terminators only: `cf.br`, `cf.cond_br`. Both use `BlockTarget` operands that may carry per-edge block arguments.
    - `Pseudo` — scaffolding ops lowered/expanded by later passes: `pseudo.copy` (universal copy — vreg→vreg pre-isel, mixed during isel, physreg→physreg post-RA, expanded to target moves last), `pseudo.merge` / `pseudo.unmerge` (artifact ops folded by the legalizer's artifact combiner), `pseudo.return` (void return shell emitted by ABI lowering; survives to isel where it is lowered to the target's return op — never reverts to `core.return`).
  - `Passes/` — all generic passes plus pass infrastructure (`Pass`, `PassManager`, `CompilationContext`, `MirFunctionPass`, `MirFunctionAnalysis`). Pipeline order in `iriec` is:
    1. `AbiLoweringPass` — drops entry-block parameters; emits per-arg-byte `AddLiveIn(physreg)` + `pseudo.copy` from the physreg into a vreg, plus `pseudo.merge` for wide arg types. Rewrites `core.return %v` to per-byte `pseudo.unmerge` + `pseudo.copy` to return physregs + `pseudo.return`. Delegates the calling-convention details to `Target.CallLowering`.
    2. `LegalizerPass` — worklist-driven legalizer: two worklists (`instList` for non-artifacts, `artifactList` for `pseudo.merge`/`pseudo.unmerge`), bottom-up initial population (LIFO pop), alternating drain — legalize each non-artifact via `Target.LegalizerInfo`, then run the file-private `LegalizationArtifactCombiner` over each artifact, repeating until empty. An `IMirObserver` wired into the builder routes builder-driven insertions to the right worklist; an `IsTriviallyDead` check at the top of each step gives DCE for free. e.g. MOS6502 legalizes `arith.addi i32` → 4× `arith.addi_with_carry i8` with the surrounding `pseudo.merge` / `pseudo.unmerge` collapsed by the combiner.
    3. `InstructionSelectorPass` — selects target opcodes via `Target.InstructionSelector`. Calls `MirFunction.ReclassifyVirtualRegister` when committing a vreg to a class; emits `pseudo.copy` into pinned physregs (e.g. `$a`, `$c`) where the target opcode requires a specific physreg. Lowers `pseudo.return` to the target's return op (e.g. `mos6502.rts`) with the live-out return physregs as implicit uses.
    4. `PhiEliminationPass` — for each terminator carrying `BlockTarget.Args`, emits per-edge `pseudo.copy` from each arg to the successor's corresponding parameter vreg (breaking cycles with a temp). Splits multi-successor edges feeding parameterized blocks into single-successor split blocks first; because this runs post-isel, a split block's unconditional branch comes from `Target.BranchLowering` (e.g. `mos6502.jmp.abs`) rather than a generic `cf.br`. After this pass: every `BlockTarget.Args` is empty and no `MirBlock.Parameters` remain.
    5. `TwoAddressInstructionPass` — for each instruction whose `DialectInstructionInfo` declares tied def/use operands, emits a `pseudo.copy` to materialize the tied use as a copy of the def-source so RA can pick a single physreg for both ends.
    6. `RegisterAllocatorPass` — **graph-colouring** register allocation with iterated register coalescing (Chaitin/Briggs/George–Appel; Appel ch. 11). The wired `MirFunctionPass` is just the IR-facing wrapper: it builds `LiveIntervals`, drives the spill loop, and applies the vreg→physreg map (then clears the vreg annotation table and recomputes block live-ins). The colouring engine is the `internal` `GraphColouringAllocator` it instantiates (Build/Simplify/Coalesce/Freeze/SelectSpill/AssignColors). A node's colours are its **class-intersection's** allocatable set (per-node K), so no class-widening pre-passes are needed; coalescing subsumes the old constraint-fixup/result-preservation/copy-hint machinery. Spilling **is** implemented: optimistic colouring, then `SpillVregs` rewrites true select-failures via rematerialization or store/reload (fresh temps marked unspillable; convergence-bounded re-colouring loop). (An earlier design was linear scan — now fully replaced.)
    7. `CopyEliminationPass` — drops identity `pseudo.copy $r → $r` and chases through trivially redundant chains. Operates on physreg-only MIR.
    8. *Target-supplied post-RA passes* — added by `Target.AddPostRegisterAllocationPasses(pm)`. For MOS6502: `MOS6502AddressingModeSelectorPass` (refines `mos6502.adc` → `mos6502.adc.zp` / `.imm` etc. based on concrete physreg operands).
    9. `PseudoExpansionPass` — replaces every remaining `pseudo.copy` with the target move emitted by `Target.PseudoExpander` (e.g. `$x = pseudo.copy $a` → `mos6502.tax`).
    - `Analyses/LivenessAnalysis` produces a `Liveness` result consumed by RA.
  - `Target/` — generic target abstractions: `Target` (exposes `Dialect`, `CallLowering`, `LegalizerInfo`, `InstructionSelector`, `PseudoExpander`, `BranchLowering`, `RegisterInfo`, `MachineCodeEmitter`, `GetRegisterName`, `AddPostRegisterAllocationPasses`, `DefaultOrigin`, `PackageImage`), `CallLowering`, `LegalizerInfo`, `InstructionSelector`, `PseudoExpander`, `BranchLowering` (supplies the target's unconditional jump for critical-edge split blocks created post-isel by `PhiEliminationPass`; mirrors LLVM's `TargetInstrInfo::insertUnconditionalBranch`), `TargetRegisterInfo`, `MachineCodeEmitter` (driver-called hook that lowers a post-PseudoExpansion `MirModule` to a `MachineCodeModule`).
  - `MachineCode/` — low-level machine-code layer; sits below MIR, above raw bytes
    - `MachineCodeModule/Function` — structural containers (no basic blocks at this layer; the body is a flat stream)
    - `MachineCodeEntry` — abstract record; two variants: `MachineCodeLabel(string Name)` and `MachineCodeInstruction(int Opcode, MachineCodeOperand[] Operands)`; labels and instructions are peers in the stream
    - `MachineCodeOperand` — abstract record; variants: `Register(int)`, `Immediate(long)`, `LabelRef(string)` (local label within the function), `ExternalRef(string)` (external symbol)
    - `MachineCode/Binary/` — structured binary: function headers + flat tagged entry stream; self-describing (no target descriptor needed to read)
  - `Target/MOS6502/` — 6502 target
    - `MOS6502Registers` — physical register IDs: `a=0, x=1, y=2, s=3, p=4, c=5, n=6, v=7, z=8, i=9, d=10, b=11`; `RC(n) = 12 + n` for imaginary zero-page registers, printed as `$zp0, $zp1, …` (RC0/RC1 reserved as soft stack pointer per CC_MOS).
    - `MOS6502Dialect` / `MOS6502Op` — full target opcode set (pre-AMS forms like `mos6502.adc`, post-AMS forms like `mos6502.adc.zp` / `.imm`, plus synthetic ops like `mos6502.bgt`).
    - `MOS6502InstructionInfo` — per-opcode `DialectInstructionInfo` (operand classes, tied operands, implicit defs/uses for flag-defining instructions).
    - `MOS6502RegisterClass` / `MOS6502RegisterInfo` — register classes (`ac`, `xc`, `yc`, `zp`, `any8`, plus single-register flag classes) and the `FlexibleI8ClassId` advertised to RA.
    - `MOS6502CallLowering` — CC_MOS convention: i8 bytes assigned in order A, X, RC2, RC3, … (LSB-first for multi-byte values).
    - `MOS6502LegalizerInfo` — declares `arith.addi i32 → NarrowScalar` (4× `arith.addi_with_carry i8`), `arith.addi i16 → NarrowScalar` (2× chain), etc.
    - `MOS6502InstructionSelector` — maps `arith.*` to target opcodes; fuses `arith.cmpi` + `cf.cond_br` into `mos6502.cmp` + `mos6502.bXX`; lowers `pseudo.return` to `mos6502.rts`.
    - `MOS6502AddressingModeSelectorPass` — target-private post-RA pass; pattern-matches `mos6502.adc` / `.cmp` / `.lda` etc. against their concrete physreg / immediate operands and rewrites the opcode.
    - `MOS6502PseudoExpander` — expands `pseudo.copy` to target moves (`mos6502.tax`, `.tay`, `.txa`, `.tya`, `.stx`, `.sty`, `.lda.zp`, `.ldx.zp`, `.ldy.zp`, `.lda.imm`, …). Impossible pairs (e.g. `$x = pseudo.copy $y`) expand via `$a`.
    - `MOS6502AssemblyWriter` / `MOS6502AssemblyParser` — traditional `$FF` / `#$FF` assembly syntax for the MachineCode layer; `MOS6502Opcode` constants equal the 6502 byte values.
    - `MOS6502MachineCodeEmitTable` — per-`MOS6502Op` rule: opcode byte, `EmitOperandKind` (`Implied` / `ZeroPageAddress` / `Immediate` / `BranchTarget` / `AbsoluteAddress`), and which MIR operand slot encodes into the byte(s). Throws on unmapped opcodes so the gap is visible the moment isel/AMS grows.
    - `MOS6502MachineCodeEmitter` — drives the table to lower a post-PseudoExpansion `MirFunction` into a `MachineCodeFunction`: emits `bbN:` labels for non-entry blocks, asks the table for each instruction's rule, and produces `MachineCodeInstruction`s with one operand (or none, for `Implied`).
    - `MOS6502BinaryEncoder` — two-pass encoder that lowers a `MachineCodeModule` to raw 6502 bytes at a given origin: layout pass assigns absolute addresses to functions and local labels, encode pass writes opcode + operand bytes, resolves `ExternalRef` / `LabelRef`, and computes signed-8-bit branch offsets. Throws on out-of-range branches and undefined symbols.
    - `MOS6502BbcMicroTarget` — subtarget inheriting from `MOS6502Target`; overrides only `DefaultOrigin` ($2000) and `PackageImage` to wrap raw bytes in a single-file Acorn DFS `.ssd` disk image via `BbcMicroDfsImage`.
    - `BbcMicroDfsImage` — internal DFS catalogue + file packager; emits a single file named `MAIN` starting at sector 2 with the minimum image size (no zero-pad to 200KB).
    - Flag registers are first-class physregs: `mos6502.cmp` defines `$n`, `$v`, `$z`; `mos6502.bgt` reads them. `pseudo.copy` is never used for flag regs.

- **Irie.Tools.Assembler** — console app (`irie-as`); reads MIR text (stdin or file), writes binary MIR
- **Irie.Tools.Disassembler** — console app (`irie-dis`); reads binary MIR (stdin or file), writes MIR text
- **Irie.Tools.Compiler** — console app (`iriec`); reads MIR text (stdin or file), runs the full pass pipeline (AbiLowering → Legalizer → InstructionSelector → PhiElimination → TwoAddressInstruction → RegisterAllocator → CopyElimination → target-supplied post-RA passes → PseudoExpansion); `--target mos6502` / `--target mos6502-bbcmicro` (both share the same chip lowering; the BBC Micro variant only differs in `--origin` default and image packaging); `--stop-after` / `--start-at` / `--run-pass` for per-pass debugging; `--emit=mir` (default — post-pipeline MIR text) / `--emit=asm` (MOS6502 assembly text via `MachineCodeEmitter`) / `--emit=mc` (structured MachineCode binary to stdout, pipeable into `irie-mc --disassemble`) / `--emit=bin` (raw bytes via `MOS6502BinaryEncoder`, then wrapped by `Target.PackageImage` — e.g. an `.ssd` for `mos6502-bbcmicro`); `--origin=<hex|decimal>` (accepts `0x1900`, `0X1900`, `$1900`, or decimal) sets the load address, falling back to `target.DefaultOrigin`.
- **Irie.Tools.MachineCode** — console app (`irie-mc`); `--assemble` parses MOS6502 assembly text → structured binary; `--disassemble` reads binary → assembly text; `--hex-dump` reads binary → xxd-style hex output (16 bytes per row, 8-digit zero-based hex offset, upper-case hex bytes with a double-space between bytes 7 and 8, forced `\n` line endings)

- **DotNetro.Compiler.Tests.CsCompiler** — helper tool used by `DotNetro.Compiler.Tests` lit tests; compiles a `.cs` source file to a `.dll` via Roslyn, piped into `dnrc`

- **DotLit** — LIT-style test infrastructure; parses `RUN:` and `CHECK:` directives from any comment line; used by both `DotNetro.Compiler.Tests` (`.cs` files, `//` comments) and `Irie.Tests` (`.irie` files for MIR tests, `.s` files for MachineCode tests; all use `;` comments)

### Irie Layer Roadmap

Two layers (unified IR landed; see [`notes/unified-ir-plan.md`](notes/unified-ir-plan.md) for the design):
1. **MIR** (`Mir/` + `Dialects/` + `Passes/` + `Target/`) — single IR that flows through every pass, starting in SSA with generic dialect ops on typed vregs and ending as non-SSA target-only ops on physregs
2. **MachineCode** (`MachineCode/`) — low-level machine-code layer (flat instruction stream + labels)

### Compilation Internals

- **Queue-based BFS method discovery:** `_methodsToVisit` queue; new methods are enqueued as call sites are encountered
- **Stack tracking:** `_stack: Stack<TypeDescription>` mirrors the IL evaluation stack to track types through codegen
- **Vtables:** `VtableTracker.BuildVtables()` builds dispatch tables for virtual calls
- **String interning:** string constants collected into `_stringTable` and emitted as data
- **Special-cased BCL methods:** `System.Console.WriteLine`, `ReadLine`, `Beep`, etc. are intercepted and mapped to BBC Micro OS calls rather than compiled normally

### Test Framework

`DotNetCompilerTests` uses a custom `[CompilerTest]` attribute. Each test method is compiled with DotNetro, then the resulting 6502 binary is executed in the Aemula 6502 CPU emulator with BBC Micro OS stubs. Output is compared against the reference .NET execution.

### External Libraries (in `/lib/`)

- **Aemula** — 6502 CPU emulator used during test execution
- **Sixty502DotNet** — 6502 assembler (Antlr4-based) used to assemble the emitted code

## MOS6502 codegen design reference (llvm-mos)

DotNetro's 6502 codegen is anchored to **llvm-mos** as the reference
implementation. When approaching a compiler **design** question for the MOS6502
target (calling convention, register allocation, frame/stack layout, lowering
choices, peephole opportunities, etc.), check **both** of these — not just one:

1. **llvm-mos output** — compile representative C with the local toolchain and
   read the generated asm / Machine IR:
   - `clang`/`llc` at `/Users/timjones/Code/llvm-mos/build/bin`
   - e.g. `clang --target=mos -Os -S -o - foo.c`; inspect Machine IR with
     `llc -stop-after=…`; use `volatile` to keep stores/loads from being
     optimized away when you want to see the access pattern.
2. **llvm-mos source code** — read the actual pass/algorithm, at
   `/Users/timjones/Code/llvm-mos` (target sources under
   `llvm/lib/Target/MOS/`, e.g. `MOSStaticStackAlloc.cpp`,
   `MOSZeroPageAlloc.cpp`, `MOSFrameLowering.cpp`, `MOSRegisterInfo.td`). The
   output shows *what* it does on one example; the source explains *why* and the
   general rule (coloring, benefit model, edge cases) that the output alone can
   hide.

## Planning workflow

Before modifying code in any area: read the relevant files first,
then construct a self-contained implementation plan that documents
what you found. Plans should not require further exploration to execute.