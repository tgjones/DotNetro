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

# Run a specific test class or method
dotnet test --project src/DotNetro.Compiler.Tests --filter "DotNetCompilerTests"
dotnet test --project src/DotNetro.Compiler.Tests --filter "FullyQualifiedName~HelloWorld"
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

- **Irie** — IR, CodeGen, and MachineCode scaffolding; not yet wired into the main compiler
  - `IR/` — SSA IR (`IRModule`, `IRFunction`, `IRBasicBlock`, `IRInstruction`); `;` starts a line comment
  - `IR/Parsing/` — text IR parser
  - `IR/Binary/` — binary serialization (`IRBinaryReader`, `IRBinaryWriter`, `IRBinaryFormat`); value references are sequential integer indices per function
  - `CodeGen/` — LLVM CodeGen-style layer; sits between IR and MachineCode; starts in SSA with generic opcodes, lowered to target-legal non-SSA by passes
    - `MachineModule/Function/BasicBlock/Instruction` — structural containers; block arguments used for SSA (no PHI instructions)
    - `MachineFunction` maintains the virtual register table (`CreateVirtualRegister`, `GetVirtualRegisterType`); parser uses `internal RegisterVirtualRegister` to create registers with caller-supplied IDs
    - `MachineOperand` — abstract record; variants: `VirtualRegisterOperand(int, bool IsDefinition)`, `PhysicalRegisterOperand(int, bool IsDefinition)`, `ImmediateOperand(long)`, `BlockOperand(MachineBasicBlock)`, `ExternalSymbolOperand(string)`; defs are first in the operand array (LLVM convention)
    - `GenericOpcode` — target-independent opcodes named `GenericAdd`, `GenericLoad`, `GenericReturn`, etc.; negative int values to distinguish from target opcodes in [0x00, 0xFF]
    - `Passes/MachineFunctionPass` — abstract pass base; `MachineFunctionPassManager` runs passes over a module or function; planned passes include IRTranslator, Legalizer, InstructionSelector, RegisterAllocator (none implemented yet)
    - `MachineWriter` / `Parsing/MachineParser` — text serialization (`.mir` format); parser is two-pass within each function: first scans for block headers (`bbN(` pattern) to pre-register all blocks, then parses instructions, enabling forward block references in branches
  - `MachineCode/` — MC layer analogous to LLVM's MCInst; sits below CodeGen, above raw bytes
    - `MachineCodeModule/Function` — structural containers (no basic blocks at this layer; the body is a flat stream)
    - `MachineCodeEntry` — abstract record; two variants: `MachineCodeLabel(string Name)` and `MachineCodeInstruction(int Opcode, MachineCodeOperand[] Operands)`; labels and instructions are peers in the stream
    - `MachineCodeOperand` — abstract record; variants: `Register(int)`, `Immediate(long)`, `LabelRef(string)` (local label within the function), `ExternalRef(string)` (external symbol)
    - `MachineCode/Binary/` — structured binary: function headers + flat tagged entry stream; self-describing (no target descriptor needed to read)
  - `Target/MOS6502/` — 6502 target; `MOS6502Opcode` constants equal the 6502 byte values; `MOS6502InstructionInfo` descriptor table; `MOS6502AssemblyWriter` / `MOS6502AssemblyParser` for traditional `$FF` / `#$FF` assembly syntax

- **Irie.Tools.Assembler** — console app (`irie-as`); reads Irie text IR (stdin or file), writes binary IR
- **Irie.Tools.CodeGen** — console app (`irie-cg`); reads Machine IR text (stdin or file), parses and reprints it (round-trip tool for LIT tests)
- **Irie.Tools.Disassembler** — console app (`irie-dis`); reads binary IR (stdin or file), writes Irie text IR
- **Irie.Tools.MachineCode** — console app (`irie-mc`); `--assemble` parses MOS6502 assembly text → structured binary; `--disassemble` reads binary → assembly text

- **DotLit** — LIT-style test infrastructure; parses `RUN:` and `CHECK:` directives from any comment line; used by both `DotNetro.Compiler.Tests` (`.cs` files, `//` comments) and `Irie.Tests` (`.irie` files for IR tests, `.mir` files for CodeGen/Machine IR tests, `.s` files for MachineCode tests; all use `;` comments)

### Irie Layer Roadmap

Three layers, analogous to LLVM:
1. **IR** (`IR/`) — SSA IR; exists today
2. **CodeGen** (`CodeGen/`) — LLVM CodeGen/`MachineInstr`-style layer; exists today; SSA in, non-SSA out
3. **MachineCode** (`MachineCode/`) — LLVM MC layer (flat instruction stream + labels); exists today

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
