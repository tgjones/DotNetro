# llvm-mos Codegen Reference

A reference corpus of small C functions compiled all the way through
[llvm-mos](https://github.com/llvm-mos/llvm-mos) (the LLVM fork that targets the
MOS 6502 and variants). It is the **known-good codegen** we measure Irie's
MOS6502 output against, across the *whole* compilation pipeline — calling
convention, legalization, instruction selection, register allocation, frame
layout, peepholes — not just register allocation.

## Why this exists

We want a body of known-good 6502 code, produced by a mature production compiler
on the **same target family** Irie targets, to:

1. **Score Irie's codegen quality** — the [`irie-report`](../../src/Irie.Tools.Reference)
   tool pairs each case here with the matching Irie test in
   [`src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/`](../../src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference)
   and reports the per-case and aggregate instruction-count delta.
2. **Drive codegen improvements** — when Irie emits more code than llvm-mos for a
   case, the `.s` (what llvm-mos produced) and `.txt` (the pass-by-pass dump
   showing *which pass* produced it) tell us the technique to reproduce. See
   *Improvement workflow* below.

This is *reference material*. The Irie `.irie` tests derived from it are a
separate suite under `src/Irie.Tests/`. Nothing here is wired into Irie's build.

## How the artifacts are produced

Per case there are three files with a shared basename, e.g. `add-i16.{c,s,txt}`:

| File   | Stage                                   | Produced by |
|--------|-----------------------------------------|-------------|
| `.c`   | Source test case                        | hand-written |
| `.s`   | **Final** MOS6502 assembly (incl. prologue/epilogue) | `clang -Os -S` |
| `.txt` | `-print-changed` dump — IR/MIR after each pass that changed it | `clang -Os -S -mllvm -print-changed` (stderr) |

Both `.s` and `.txt` fall out of a **single** clang invocation
([`build.sh`](build.sh)):

```bash
clang --target=mos -Os -S -mllvm -print-changed foo.c -o foo.s 2>foo.txt
```

The toolchain used was the local llvm-mos build at
`/Users/timjones/Code/llvm-mos/build/bin/clang`
(`clang version 23.0.0git`, target `mos-unknown-unknown`).

### Regenerating everything

```bash
./build.sh                 # uses -Os and the path above
OPT=-O2 ./build.sh         # override opt level
CLANG=… ./build.sh         # override toolchain
```

### A note on optimization level

We standardized on **`-Os`** (CLAUDE.md's reference opt level for llvm-mos).
Empirically, `-O2`, `-Os` and `-Oz` produce nearly identical code for these tiny
functions: by the time codegen reaches the 6502 backend, allocation and lowering
are dominated by the tiny physical register set and the imaginary zero-page
register file, not by the size-vs-speed tradeoff. `-O0` is *not* used: it spills
everything to the stack and exercises no interesting codegen.

### `.s` is the final word; `.txt` shows how it got there

The `.s` file is the truly-final assembly — it includes the prologue/epilogue
(callee-saved `$rc*` save/restore) inserted late by Prologue/Epilogue Insertion.
This is the right thing to count instructions against and to compare with Irie's
fully-lowered `--emit=asm` output.

The `.txt` file is the `-print-changed` dump: the IR/Machine-IR after each pass,
but printed in full *only* for passes that actually changed it — passes that made
no change collapse to a one-line `*** IR Dump After <PassName> … omitted because
no change ***` marker, so you still see the complete pass order. (This is ~4×
smaller than `-print-after-all`.) It stops *before* assembly emission (which is
why `.s` is a separate file). Use it to find which pass introduced a sequence you
want to understand — search for the pass header `*** IR Dump After <PassName>`
and read the MIR that follows. Key machine passes to look for: `Legalizer`,
`InstructionSelect`, `Greedy Register Allocator`, `Virtual Register Rewriter`,
`Prologue/Epilogue Insertion`.

## Reading the MIR (and how it maps to Irie)

llvm-mos MIR is strikingly close to Irie's own MIR, which is what makes this
corpus useful. Key correspondences:

| llvm-mos MIR | Irie equivalent / meaning |
|--------------|----------------------------|
| `$a`, `$x`, `$y` | the three real 6502 registers (`a`, `x`, `y`) |
| `$rc0`, `$rc1`, … | **imaginary zero-page registers** — same idea as Irie's `RC(n)` / `$zp*`. A large register *file* in zero page; the first line of pressure relief before the soft stack. `$rc0/$rc1` are reserved as the soft-stack pointer. |
| `$c`, `$n`, `$v`, `$z` | flag registers as first-class physregs (cf. Irie's `mos6502.cmp` defining `$n/$v/$z`). Never spilled. |
| `COPY` | `pseudo.copy` (universal copy, physreg→physreg at this stage) |
| `ADCImag8` | the narrow add-with-carry primitive — cf. Irie's `arith.addi_with_carry`. Multi-byte adds are a chain of these threading `$c`. |
| `STStk` / `LDStk` | **genuine spill** store/load to the soft stack (`%stack.N`). |
| `liveins: $a, $x, …` | block live-ins — same concept as `MirBlock.LiveIns` |
| `renamable` / `killed` / `tied-def N` | reassignable operand / last use / two-address constraint (cf. Irie's `TwoAddressInstructionPass`) |
| `JSR @g, …, implicit $a, implicit-def $a` | call with explicit live-in args and clobber set (caller-saved model) |
| `bb.N`, `successors:`, `JMP`/`GBR`/`CmpBrZero` | basic blocks + terminators (cf. `cf.br` / `cf.cond_br`) |

Type widths follow llvm-mos C conventions: **`char` = i8, `int` = i16, `long` =
i32, `long long` = i64**, pointers are 16-bit. Wider types become longer byte
chains and more simultaneous live byte-values — i.e. more register pressure.

### Where the interesting behaviour shows up

- **Soft-stack spills (`STStk`/`LDStk`)** are *rare* — the zero-page register
  file is large and absorbs most pressure. The clearest example is
  [`pressure/many-calls`](pressure/many-calls.c). Wide-type cases
  ([`pressure/pressure-high`](pressure/pressure-high.c),
  [`pressure/pressure-i64`](pressure/pressure-i64.c)) instead climb deep into the
  `$rc*` file without hitting the stack — that *is* the llvm-mos pressure-relief
  story.
- **Copy cycles** (swap / value rotation) show up most richly in
  [`realistic/fibonacci`](realistic/fibonacci.c) and
  [`realistic/gcd`](realistic/gcd.c), where the loop back-edge rotates a pair of
  values through several `$rc*` registers.
- **Calls** (`JSR`) appear not just in the explicit-call cases but anywhere a
  multiply/divide/modulo is lowered to a runtime helper (e.g.
  `basics/chain-arith`, `control-flow/nested-loop`).

## Improvement workflow

The [`irie-report`](../../src/Irie.Tools.Reference) `report.html` is the
scoreboard. To close a gap:

1. Build `iriec`, run `irie-report`, open `report.html`; sort by worst ratio.
2. For the worst case, read its `.s` (what llvm-mos emitted) and `.txt` (the
   `-print-changed` dump pinpoints which pass produced the better sequence).
   Per CLAUDE.md, also read the responsible llvm-mos source pass under
   `llvm/lib/Target/MOS/`.
3. Get the symmetric view of *Irie's* pipeline on the same case by running the
   matching `.irie` test through `iriec --print-changed` — the Irie analogue of
   the llvm-mos `.txt`. Like clang's `-print-changed`, it lists every pass but
   only dumps the MIR for passes that changed it; unchanged passes collapse to a
   one-line `*** MIR Dump After <Pass> omitted because no change ***` marker, so
   the full pass order stays visible. Dumps go to stderr, so you can read Irie's
   per-pass behaviour side-by-side with llvm-mos's and see exactly where the two
   pipelines diverge:

   ```bash
   # dumps go to stderr; --emit=asm keeps the final asm on stdout
   iriec --target mos6502 --emit=asm --print-changed \
       src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/<case>.irie 2>&1 >/dev/null
   ```

   (Use `--print-after-all` instead to dump the MIR in full after every pass,
   including the ones that made no change.)
4. Improve the corresponding Irie pass; re-run that case's lit test under
   `src/Irie.Tests/.../LlvmMosReference/`, regenerate its golden `CHECK` block,
   re-run the report, and watch the headline ratio move.
5. The full lit suite + golden `CHECK` blocks catch regressions across all cases.

## Case index

46 cases across eight categories. Each name links to its source; the `.s` and
`.txt` siblings live alongside it.

### `basics/` — calling convention & simple arithmetic
- [`ret-const`](basics/ret-const.c) — materialize a constant return value
- [`identity-i8`](basics/identity-i8.c) — trivial i8 passthrough (no moves ideal)
- [`add-i8`](basics/add-i8.c) — single-byte two-address add on `$a`
- [`add-i16`](basics/add-i16.c) — 2-byte carry chain
- [`add-i32`](basics/add-i32.c) — 4-byte carry chain, spills into `$rc*`
- [`sub-i16`](basics/sub-i16.c) — non-commutative: operand order is fixed
- [`many-args`](basics/many-args.c) — six i16 args exhaust the live-in registers
- [`stack-args`](basics/stack-args.c) — ten i16 args overflow to soft-stack-passed arguments
- [`chain-arith`](basics/chain-arith.c) — overlapping live ranges, `a` reused across a multiply

### `coalescing/` — copies & cycles
- [`copy-passthrough`](coalescing/copy-passthrough.c) — copy chain a→x→y→z
- [`swap`](coalescing/swap.c) — value swap = copy cycle the coalescer must break
- [`commutative`](coalescing/commutative.c) — commutativity as a coalescing degree of freedom
- [`redundant-copy`](coalescing/redundant-copy.c) — phi at a join wants both inputs in one reg

### `pressure/` — register pressure & spilling
- [`pressure-i16`](pressure/pressure-i16.c) — moderate, fits the register file
- [`pressure-high`](pressure/pressure-high.c) — wide-type, long live ranges, deep `$rc*` use
- [`live-across-call`](pressure/live-across-call.c) — one value survives a clobbering call
- [`many-calls`](pressure/many-calls.c) — four values across four calls → `STStk` spills
- [`pressure-i64`](pressure/pressure-i64.c) — 8-byte values, heaviest carry + pressure

### `control-flow/` — branches, loops, switch
- [`if-else`](control-flow/if-else.c) — diamond + phi merge
- [`select`](control-flow/select.c) — ternary, often branchless select
- [`loop-counter`](control-flow/loop-counter.c) — loop-carried accumulator + induction var
- [`loop-sum-array`](control-flow/loop-sum-array.c) — pointer/index + accumulator across back edge
- [`nested-loop`](control-flow/nested-loop.c) — two induction vars across two back edges
- [`switch`](control-flow/switch.c) — wide shallow CFG merging to one return
- [`early-return`](control-flow/early-return.c) — guard clauses, staggered live-range ends

### `constraints/` — register-specific ops, flags
- [`inc-dec`](constraints/inc-dec.c) — INC/DEC vs INX/INY register pinning
- [`shift-const`](constraints/shift-const.c) — ASL/LSR on `$a` + carry
- [`shift-var`](constraints/shift-var.c) — variable shift → loop with pinned accumulator
- [`compare-flags`](constraints/compare-flags.c) — chained compares, flag-register liveness
- [`bitops`](constraints/bitops.c) — AND/ORA/EOR two-address chain on `$a`

### `memory/` — pointers, arrays, structs, globals
- [`ptr-deref`](memory/ptr-deref.c) — load/store through a zp pointer pair
- [`array-index`](memory/array-index.c) — indexed load, base in pointer class + `$y` index
- [`struct-pass`](memory/struct-pass.c) — small struct by value spread across regs
- [`struct-return`](memory/struct-return.c) — multiple live-out values into return regs
- [`struct-return-sret`](memory/struct-return-sret.c) — large struct returned via a hidden `sret` pointer pair
- [`two-pointer-copy`](memory/two-pointer-copy.c) — two live pointer-pairs + X/Y index pressure
- [`global-rw`](memory/global-rw.c) — absolute addressing, no register wasted on the address

### `widths/` — width conversions
- [`zero-extend`](widths/zero-extend.c) — high byte is a constant 0
- [`sign-extend`](widths/sign-extend.c) — high byte derived from the sign bit
- [`truncate`](widths/truncate.c) — high byte's live range ends early
- [`mixed-widths`](widths/mixed-widths.c) — i8/i16/i32 overlapping live ranges

### `realistic/` — small whole routines
- [`fibonacci`](realistic/fibonacci.c) — loop-carried pair rotation (copy cycle in a loop)
- [`gcd`](realistic/gcd.c) — loop + modulo helper call + swap
- [`strlen`](realistic/strlen.c) — pointer-walking loop, pointer-class liveness
- [`factorial-recursive`](realistic/factorial-recursive.c) — spill around a recursive call + multiply
- [`crc8`](realistic/crc8.c) — nested loop, bit tests, shifts, conditional XOR — dense all-axis stressor
