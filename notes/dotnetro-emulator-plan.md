# DotNetro emulator CLI plan

Build a command-line tool, `DotNetro.Compiler.Tests.Emulator`, that
runs a compiled DotNetro binary in the same Aemula-driven 6502 host
that [`ExecuteDotNetro` in DotNetCompilerTests.cs](../src/DotNetro.Compiler.Tests/DotNetCompilerTests.cs#L396)
runs today, but as a stand-alone process so it composes with our
Lit-style test setup.

The tool is named generically (no `BbcMicro` / `Mos6502` in the name)
so the same binary can grow a Z80 target later. The active system is
selected with `--target-system bbcmicro`.

## 1. Scope and non-goals

In scope:

- A new console project at [`src/DotNetro.Compiler.Tests.Emulator/`](../src/DotNetro.Compiler.Tests.Emulator)
  that references `Aemula.Chips.Mos6502` and exposes a `--target-system`
  flag (initially only `bbcmicro`).
- Reading a raw 6502 program from stdin or a file path, loading it at
  the target's conventional load address (`$2000` for BBC Micro), and
  executing it with stubbed OS calls until the program returns.
- Writing emitted console output to stdout, so a Lit `CHECK:` line can
  match against it.
- A new `--emit program` option in
  [`DotNetro.Compiler.Driver`](../src/DotNetro.Compiler.Driver/Program.cs)
  so the existing `dnrc` can produce the raw-program bytes the
  emulator consumes. (Today `--emit Executable` produces the SSD-wrapped
  `CompiledImage`; the emulator wants the unwrapped `CompiledProgram`.)
- Hooking the tool into
  [`LitTests.cs`](../src/DotNetro.Compiler.Tests/LitTests.cs) under the
  substitution name `emulator`, mirroring the existing
  `cs_compiler` / `dnrc` wiring of `DotNetro.Compiler.Tests.CsCompiler`.

Out of scope:

- A full BBC Micro machine emulator. We stub only the few OS entry
  points that the existing in-process test harness stubs.
- A Z80 target. The CLI surface and internal abstractions are shaped
  to accept one, but no Z80 code lands here.
- Migrating the existing `DotNetCompilerTests.CompilerTests` to drive
  the new tool as a subprocess. The in-process execution path stays as
  it is; switching it over is a separate decision (see §8).

## 2. CLI surface

```
DotNetro.Compiler.Tests.Emulator [options] [<program-file>]

Arguments:
  <program-file>           Path to a raw 6502 program file, or - for stdin.
                           Defaults to stdin if omitted.

Options:
  --target-system <name>   Target system. Currently: bbcmicro (default).
  --input <string>         Console input. Use \r to separate lines, matching
                           the convention in CompilerTestAttribute.ConsoleInputs.
                           Example: --input "Foo\rBar".
  --max-ticks <int>        Tick budget before the run is aborted as a runaway.
                           Default: 1_000_000. The existing in-process harness
                           uses 30_000 but that has been tight for some tests
                           — start generous, tune if CI is too slow.
  --trace <file>           Write a per-instruction PC/A/X/Y/P/SP trace to <file>.
                           Off by default. Mirrors the debugWriter output in
                           ExecuteDotNetro.
  --                       Stop option parsing.
```

Exit codes:
- `0` — program ran to completion (hit the "done" marker for the target).
- `1` — argument / I/O error.
- `2` — runaway: `--max-ticks` exceeded before completion.
- `3` — emulator-internal error (e.g. unhandled OS call).

Stdout is reserved for the emulated program's console output. All
diagnostics, banners, and the trace go to stderr (or `--trace`).
This is what lets a Lit `CHECK:` line match cleanly:

```
; RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
; CHECK: Hello, World!
Console.WriteLine("Hello, World!");
```

## 3. `dnrc` additions

[`DotNetro.Compiler.Driver/Program.cs`](../src/DotNetro.Compiler.Driver/Program.cs)
currently emits `Assembly` or `Executable`. Add a third value to the
`EmitFormat` enum:

```csharp
enum EmitFormat
{
    Assembly,
    Program,     // NEW: raw CompiledProgram bytes (no SSD wrapper)
    Executable,
}
```

In the action, the `Program` branch writes
`compilationResult.CompiledProgram.ToArray()` to the output stream
without the `.asm` / `.lst` side files. That is the byte stream the
emulator loads at `$2000`.

This addition is small and contained; no rename of the existing
`Executable` value, so no migration cost.

## 4. Project layout

Create [`src/DotNetro.Compiler.Tests.Emulator/`](../src/DotNetro.Compiler.Tests.Emulator)
with:

```
DotNetro.Compiler.Tests.Emulator.csproj
Program.cs
TargetSystems/
  ITargetSystem.cs
  BbcMicroTargetSystem.cs
```

`DotNetro.Compiler.Tests.Emulator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$(DotNetVersion)</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Aemula.Chips.Mos6502">
      <HintPath>..\..\lib\Aemula\Aemula.Chips.Mos6502.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

Add the project to [`DotNetro.slnx`](../src/DotNetro.slnx) alongside
the other tool projects.

## 5. Internal architecture

### 5.1 `ITargetSystem`

A small abstraction that captures everything the emulator host needs
to know about a target. Implementations are stateless apart from their
own configuration; the per-run state (memory, CPU, output buffer)
lives in `Program.cs`.

```csharp
internal interface ITargetSystem
{
    string Name { get; }

    ushort LoadAddress { get; }     // e.g. 0x2000 for bbcmicro

    // Sets up reset vector, OS entry stubs, and any other fixed memory
    // the program assumes is present. Called once after the program
    // bytes have been copied into memory.
    void InitialiseMemory(byte[] memory);

    // Runs the CPU until completion. Returns the captured console output.
    EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks);
}

internal sealed record EmulationResult(string Output, EmulationStatus Status);

internal enum EmulationStatus { Completed, RunawayTimeout }
```

Splitting `Run` per-target rather than sharing a generic CPU loop lets
the BBC Micro implementation dispatch on the 6502's `Pins.Sync` and PC,
while a future Z80 target can use Aemula's Z80 chip with its own pin
shape. The shared driver in `Program.cs` only wires arguments,
chooses an `ITargetSystem`, and prints the result.

### 5.2 `BbcMicroTargetSystem`

A direct port of the existing
[`ExecuteDotNetro` setup](../src/DotNetro.Compiler.Tests/DotNetCompilerTests.cs#L396):

- `LoadAddress = 0x2000`.
- `InitialiseMemory` writes:
  - `$FFEE = RTS` (`OSWRCH` stub).
  - `$FFE3 = RTS` (`OSASCI` stub).
  - `$FFF1 = RTS` (`OSWORD` stub).
  - Reset vector at `$FFFC/D` → `$FF00`.
  - `$FF00 = JSR $2000 ; RTS` so the CPU enters the program after reset
    and falls back to `$FF03` (the end marker) when the program returns.
- `Run` ticks the 6502 in the same loop the existing harness uses:
  - On `pins.RW` true, `pins.Data = memory[address]`. Otherwise
    `memory[address] = pins.Data`.
  - On `Pins.Sync`, optionally emit a trace line to the trace writer.
  - Switch on `cpu.PC`:
    - `$FF03` → terminate, return collected output.
    - `$FFE3` → `output.Append((char)cpu.A)` (OSASCI).
    - `$FFF1` with `cpu.A == 0` → OSWORD 0: read one line from the
      input reader, write it (CR-terminated) to the address held at
      zero-page `$37/$38`.
  - Bail with `RunawayTimeout` once tick count exceeds `maxTicks`.
- `OSWRCH` (`$FFEE`) is currently *not* observed by the in-process
  test (it relies on OSASCI for character output). We preserve that
  behaviour — `OSWRCH` stays as a plain RTS stub. If a future test
  produces output via `OSWRCH`, the case will be added.

The whole class is ~80 lines, and is the only place that knows about
the 6502 / BBC Micro.

### 5.3 `Program.cs`

System.CommandLine wiring, mirroring
[`DotNetro.Compiler.Driver/Program.cs`](../src/DotNetro.Compiler.Driver/Program.cs):

1. Parse `<program-file>` argument (defaults to stdin, supports `-`).
2. Read the program bytes; check it fits inside
   `[LoadAddress, 0xFFFF]`. Error out if not.
3. Resolve the target system. Today, a single switch on
   `--target-system`:
   ```csharp
   ITargetSystem target = targetSystemName switch
   {
       "bbcmicro" => new BbcMicroTargetSystem(),
       _ => throw new ArgumentException($"Unknown --target-system '{targetSystemName}'."),
   };
   ```
4. Allocate `byte[0x10000]`, copy program bytes at `target.LoadAddress`,
   call `target.InitialiseMemory(memory)`.
5. Translate `--input` into a `TextReader` that yields the requested
   lines. Escape sequence: only `\r` is interpreted (matching how
   `CompilerTestAttribute.ConsoleInputs` is joined with `\r` today).
6. Translate `--trace <file>` into an optional `StreamWriter`.
7. Call `target.Run(...)` and write its `Output` to `Console.Out`.
8. Return the appropriate exit code based on `EmulationStatus`.

## 6. LitTests integration

Edit
[`LitTests.cs`](../src/DotNetro.Compiler.Tests/LitTests.cs) to add an
`emulator` substitution and add a `ProjectReference` (with
`ReferenceOutputAssembly="false"`) to the new project from
[`DotNetro.Compiler.Tests.csproj`](../src/DotNetro.Compiler.Tests/DotNetro.Compiler.Tests.csproj),
matching the existing CsCompiler pattern.

```csharp
LitTestRunner.Run(filePath, new LitTestConfiguration(new Dictionary<string, string>
{
    ["cs_compiler"] = ...,
    ["dnrc"]        = ...,
    ["emulator"]    = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        $"../../../../DotNetro.Compiler.Tests.Emulator/bin/{buildConfig}/net10.0/DotNetro.Compiler.Tests.Emulator")),
}.ToImmutableDictionary()));
```

Once that is in place, the kinds of Lit tests that become possible:

```csharp
// File: src/DotNetro.Compiler.Tests/Lit/HelloWorld.cs

// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: Hello, World!
Console.WriteLine("Hello, World!");
```

```csharp
// File: src/DotNetro.Compiler.Tests/Lit/ReadAndWriteLine.cs

// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --input "Foo"
// CHECK: Enter some text:
// CHECK-NEXT: You said:
// CHECK-NEXT: Foo
Console.WriteLine("Enter some text:");
var text = Console.ReadLine();
Console.WriteLine("You said:");
Console.WriteLine(text);
Console.Beep();
```

The existing single Lit test
([`Lit/HelloWorld.cs`](../src/DotNetro.Compiler.Tests/Lit/HelloWorld.cs))
covers the assembly-emit path. The new tool gives Lit tests an
end-to-end path that exercises codegen + assembler + execution, which
complements (but does not replace) the `DotNetCompilerTests` in-process
suite.

## 7. Stepwise landing order

Each step compiles and tests stay green between PRs.

1. **Add `--emit program` to `dnrc`.** One enum value, one switch arm,
   no new project. Verify by piping `dnrc --emit program` through
   `xxd` and confirming the leading bytes match `CompiledProgram` for
   a known test method. No new Lit test yet.

2. **Create `DotNetro.Compiler.Tests.Emulator` project skeleton.** New
   csproj + empty `Program.cs` that just parses args and prints "not
   implemented". Add to `DotNetro.slnx`. Confirm it builds via
   `dotnet test --solution src` (which builds it transitively once
   the test project gets a `ProjectReference` to it in step 4).

3. **Port the BBC Micro emulator loop.** Implement
   `ITargetSystem` + `BbcMicroTargetSystem` with the loop from
   `ExecuteDotNetro`. Wire `--target-system`, `--input`, `--max-ticks`,
   `--trace`. Sanity-check by hand with a known input file.

4. **Hook into LitTests.** Add the `emulator` substitution to
   `LitTests.cs` and the `ProjectReference` to the test csproj. Add one
   new Lit test (`Lit/HelloWorldEmulated.cs`) that uses the full
   pipeline. CI now exercises the emulator end-to-end on every test
   run.

5. **(Optional) Port more `[CompilerTest]` cases as Lit tests.** Each
   one a one-line Lit file. This is where the new harness starts to
   pay for itself — Lit tests are textual, easy to read, and don't
   require Reflection over the test assembly. The existing
   in-process tests stay; they're authoritative for the .NET-reference
   diff today.

## 8. Open questions / future work

1. **Switching the in-process `CompilerTests` to use the subprocess.**
   Once the emulator tool is mature, the `ExecuteDotNetro` body could
   shell out to `DotNetro.Compiler.Tests.Emulator` instead of running
   the Aemula loop in-process. Pros: one execution path to maintain,
   one place to fix bugs. Cons: per-test subprocess overhead (which
   for 20+ tests may be noticeable). Defer — decide once we measure.

2. **Z80 / future-target hook.** When a Z80 target lands, it adds a
   new `ITargetSystem` implementation (probably gated on `Aemula.Chips.Z80`
   becoming available), a new arm in the `--target-system` switch in
   `Program.cs`, and nothing else. The `ITargetSystem.Run` signature
   is deliberately not tied to 6502 pin shapes.

3. **OSWRCH stub vs. OSASCI stub.** Today only OSASCI is observed for
   console output. If/when DotNetro emits OSWRCH-based output, add a
   sibling case in the `BbcMicroTargetSystem.Run` switch. Trivial; no
   plan change.

4. **Realistic BBC Micro emulator.** The current stubs are very
   minimal — a real BBC Micro emulator inside Aemula would let us
   exercise the SSD output directly, but that is much larger scope and
   not required for the testing gap this plan closes.

5. **Trace format.** The proposed trace format is the same one-line-
   per-instruction format
   [`ExecuteDotNetro` already writes](../src/DotNetro.Compiler.Tests/DotNetCompilerTests.cs#L451)
   to `CompilerTest<Name>.out`. Keeping the format identical means
   existing tooling that consumes those traces (if any — currently
   only humans reading them on failure) continues to work.
