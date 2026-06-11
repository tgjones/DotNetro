# llvm-mos Register Allocator Corpus

A reference corpus of register-allocation scenarios, captured from
[llvm-mos](https://github.com/llvm-mos/llvm-mos) (the LLVM fork that targets the
MOS 6502 and variants).

## Why this exists

We are about to revisit Irie's `RegisterAllocatorPass`, which has grown sprawling
and may be due for a redesign. Before touching it, we want a body of *known-good*
register-allocation examples to anchor the work — produced by a mature, production
allocator on the **same target family** (MOS 6502) that Irie targets.

Each case here is a small C function compiled down to Machine IR (MIR) **with
register allocation already performed**. These MIR outputs are intended to become
the basis for `.irie` tests in our own suite: we can study how llvm-mos places
values, breaks copy cycles, threads carry flags, spills under pressure, and so on,
and use that to specify and validate Irie's allocator behaviour.

This corpus is *reference material*, not a drop-in test suite. The `.irie` tests
derived from it are a separate, later step. Nothing here is wired into Irie yet.

## How the artifacts are produced

Per case there are three files with a shared basename, e.g. `add-i16.{c,ll,mir}`:

| File   | Stage                              | Produced by |
|--------|------------------------------------|-------------|
| `.c`   | Source test case                   | hand-written |
| `.ll`  | LLVM IR (optimized, pre-codegen)   | `clang -O2 -S -emit-llvm` |
| `.mir` | Machine IR, **after** register allocation | `llc -stop-after=virtregrewriter` |

Exact commands (also encoded in [`build.sh`](build.sh)):

```bash
clang -O2 -S -emit-llvm foo.c -o foo.ll
llc  -stop-after=virtregrewriter foo.ll -o foo.mir
```

The toolchain used was the local llvm-mos build at
`/Users/timjones/Code/llvm-mos/build/bin/{clang,llc}`
(`clang version 23.0.0git`, target `mos-unknown-unknown`).

### Why `-stop-after=virtregrewriter`?

llvm-mos performs register allocation in **two steps**, and we want the point at
which the result is fully baked into the instruction stream:

1. **Greedy Register Allocator** — assigns each virtual register to a physical
   register (or a spill slot), making the live-range-splitting, eviction and
   spill decisions. It works on a side table; the MIR still references vregs.
2. **Virtual Register Rewriter** (`virtregrewriter`) — rewrites the MIR so every
   virtual-register operand is replaced by its assigned physical register, drops
   identity copies, and records the final `liveins`/`killed` flags.

Stopping *after* `virtregrewriter` gives us MIR with **no virtual registers
left** — only physical registers (`$a`, `$x`, `$y`, `$rc*`, flag regs) and any
spill traffic. That is the closest analogue to the state of Irie's MIR *after*
`RegisterAllocatorPass` runs, which is exactly what we want to compare against.

### Regenerating everything

```bash
./build.sh                 # uses -O2 and the path above
OPT=-Oz ./build.sh         # override opt level
CLANG=… LLC=… ./build.sh   # override toolchain
```

### A note on optimization level

We standardized on **`-O2`**. Empirically, `-O2`, `-Os` and `-Oz` produce
**byte-identical MIR** on llvm-mos for these cases (verified on several,
including ones that spill). By the time codegen reaches `virtregrewriter` on
6502, allocation is dominated by the tiny physical register set and the imaginary
zero-page register file, not by the size-vs-speed opt tradeoff — so a multi-level
matrix would only produce duplicate files. `-O0` is *not* used: it spills
everything to the stack and exercises no interesting allocation.

## Reading the MIR (and how it maps to Irie)

llvm-mos MIR is strikingly close to Irie's own MIR, which is what makes this
corpus useful. Key correspondences:

| llvm-mos MIR | Irie equivalent / meaning |
|--------------|----------------------------|
| `$a`, `$x`, `$y` | the three real 6502 registers (`a`, `x`, `y`) |
| `$rc0`, `$rc1`, … | **imaginary zero-page registers** — same idea as Irie's `RC(n)` / `$zp*`. This is a large register *file* living in zero page; it is the first line of pressure relief before the soft stack. `$rc0/$rc1` are reserved as the soft-stack pointer. |
| `$c`, `$n`, `$v`, `$z` | flag registers as first-class physregs (cf. Irie's `mos6502.cmp` defining `$n/$v/$z`). Never spilled. |
| `COPY` | `pseudo.copy` (universal copy, physreg→physreg at this stage) |
| `ADCImag8` | the narrow add-with-carry primitive — cf. Irie's `arith.addi_with_carry`. Multi-byte adds are a chain of these threading `$c`. |
| `STStk` / `LDStk` | **genuine spill** store/load to the soft stack (`%stack.N`). The clearest sign the register file was exhausted. |
| `liveins: $a, $x, …` | block live-ins — same concept as `MirBlock.LiveIns` |
| `renamable` | operand may be reassigned to a different physreg (pre-rewrite vreg origin); informational at this stage |
| `killed` | last use of a value in that register — the live range ends here |
| `tied-def N` | two-address constraint — cf. Irie's `TwoAddressInstructionPass` tied operands |
| `JSR @g, …, implicit $a, implicit-def $a` | call with explicit live-in args and clobber set (caller-saved model) |
| `bb.N`, `successors:`, `JMP`/`GBR`/`CmpBrZero` | basic blocks + terminators (cf. `cf.br` / `cf.cond_br`) |

Type widths follow llvm-mos C conventions: **`char` = i8, `int` = i16, `long` =
i32, `long long` = i64**, pointers are 16-bit. Wider types become longer byte
chains and more simultaneous live byte-values — i.e. more register pressure.

### Where the interesting behaviour shows up

- **Genuine soft-stack spills (`STStk`/`LDStk`)** are *rare*, because the
  zero-page register file is large and absorbs most pressure. The clearest
  example in the corpus is [`pressure/many-calls`](pressure/many-calls.mir)
  (four values forced to survive four calls). The wide-type pressure cases
  ([`pressure/pressure-high`](pressure/pressure-high.mir),
  [`pressure/pressure-i64`](pressure/pressure-i64.mir)) instead climb deep into
  the `$rc*` file (up to `$rc31`) without hitting the stack — that *is* the
  llvm-mos pressure-relief story, and worth studying as such.
- **Copy cycles** (swap / value rotation) show up most richly in
  [`realistic/fibonacci`](realistic/fibonacci.mir) and
  [`realistic/gcd`](realistic/gcd.mir), where the loop back-edge rotates a pair
  of values through several `$rc*` registers.
- **Calls** (`JSR`) appear not just in the explicit-call cases but anywhere a
  multiply/divide/modulo is lowered to a runtime helper (e.g.
  `basics/chain-arith`, `control-flow/nested-loop`).

### Behaviours that are deliberately under-covered, and one snapshot caveat

Some classic register-allocator behaviours turn out to be **rare or absent** on
this target, and the corpus reflects that rather than contriving them:

- **Rematerialization and live-range splitting/spilling are rare.** The
  imaginary zero-page register file is large *and* partly callee-saved, so
  values that would be spilled or rematerialized on a register-poor machine are
  instead simply kept in `$rc*` across calls. Attempts to force rematerialization
  also tend to fold the value into an immediate operand (e.g. `EOR #$12`), so it
  never needs a register at all. `pressure/many-calls` is the clearest case that
  reaches genuine soft-stack spills; treat true spilling/splitting as the
  exception here, not the norm.
- **Snapshot caveat — callee-saved save/restore is not present yet.** We stop
  after `virtregrewriter`, but the prologue/epilogue that saves and restores
  callee-saved `$rc*` registers is inserted *later* by Prologue/Epilogue
  Insertion. So a value that survives several calls may show **no** spill code in
  these `.mir` files even though the final program will save it — the allocation
  decision is visible, the callee-saved spill code is not. Keep this in mind when
  comparing instruction counts against a fully lowered Irie function.

Not modelled at all (out of scope for what Irie reproduces from C): inline-asm
register clobbers, soft-float, and varargs.

## Case index

46 cases across eight categories. Each name links to its source; the `.ll` and
`.mir` siblings live alongside it.

### `basics/` — calling convention & simple arithmetic
- [`ret-const`](basics/ret-const.c) — materialize a constant return value
- [`identity-i8`](basics/identity-i8.c) — trivial i8 passthrough (no moves ideal)
- [`add-i8`](basics/add-i8.c) — single-byte two-address add on `$a`
- [`add-i16`](basics/add-i16.c) — 2-byte carry chain
- [`add-i32`](basics/add-i32.c) — 4-byte carry chain, spills into `$rc*`
- [`sub-i16`](basics/sub-i16.c) — non-commutative: operand order is fixed
- [`many-args`](basics/many-args.c) — six i16 args exhaust the live-in registers
- [`stack-args`](basics/stack-args.c) — ten i16 args overflow to soft-stack-passed arguments (`fixedStack`/`AddrLostk`)
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
- [`many-calls`](pressure/many-calls.c) — four values across four calls → **`STStk` spills**
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
- [`struct-return`](memory/struct-return.c) — multiple live-out values into return regs (fits in regs)
- [`struct-return-sret`](memory/struct-return-sret.c) — large struct returned via a hidden `sret` pointer pair (`STIndirIdx`)
- [`two-pointer-copy`](memory/two-pointer-copy.c) — two live pointer-pairs (`$rs1`+`$rs2`) + X/Y index pressure
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
