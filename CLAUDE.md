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

- **Irie** — IR scaffolding (`IRModule`, `IRFunction`, `IRBasicBlock`, `IRInstruction`); not yet wired into the main compiler
  - `IR/Parsing/` — text IR parser; `;` starts a line comment
  - `IR/Binary/` — binary serialization (`IRBinaryReader`, `IRBinaryWriter`, `IRBinaryFormat`); value references are sequential integer indices per function, same scheme as the text format

- **Irie.Tools.Assembler** — console app (`irie-as`); reads Irie text IR (stdin or file), writes binary
- **Irie.Tools.Disassembler** — console app (`irie-dis`); reads binary (stdin or file), writes Irie text IR

- **DotLit** — LIT-style test infrastructure; parses `RUN:` and `CHECK:` directives from any comment line; used by both `DotNetro.Compiler.Tests` (`.cs` files, `//` comments) and `Irie.Tests` (`.irie` files, `;` comments)

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
