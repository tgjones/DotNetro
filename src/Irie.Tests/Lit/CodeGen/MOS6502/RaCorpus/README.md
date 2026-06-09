# RaCorpus — register-allocator tests derived from the llvm-mos corpus

These `.irie` lit tests are derived from the reference corpus in
[`design/register-allocator/reference-register-allocator/`](../../../../../../design/register-allocator/reference-register-allocator).
That corpus is C → llvm-mos Machine IR (what a mature allocator produces);
these tests pin what **Irie's** `RegisterAllocatorPass` produces for the same
computation, so we can track convergence/divergence as the allocator is
redesigned.

## Convention

Each test:
- `; RUN: @iriec --target mos6502 --stop-after RegisterAllocator @file`
- a header comment naming the source corpus case, the llvm-mos behaviour of
  note, and any divergence;
- a `; CHECK:` block pinning Irie's **actual** current post-RA output (these are
  characterization/golden tests — they document today's behaviour, including
  warts, so the redesign has a baseline to diff against);
- the input MIR: pre-pipeline generic-dialect MIR (`arith.*`, `cf.*`,
  `mem.*`, `call.func`, `core.return`).

The CHECK blocks were generated mechanically (run iriec, regex-escape each
output line); they are not hand-written.

> Note: vregs in the input MIR must be **numeric** (`%0`, `%1`, …) — the MIR
> lexer rejects named vregs. CHECK lines are regex-escaped and matched in order.

## What converted, and what didn't

Only a subset of the 46 corpus cases can currently be expressed in Irie's
generic MIR *and* survive the pipeline. The blockers below are not corpus
problems — they are **Irie's current limitations**, and this table doubles as a
punch-list for the RA redesign (and the surrounding isel/legalizer work).

### Converted (17 test files, covering ~19 corpus cases)

| Corpus case | Test |
|-------------|------|
| basics/ret-const | `RetConstI16-RegisterAllocator` |
| basics/identity-i8 | `IdentityI8-RegisterAllocator` |
| basics/add-i16 | `AddI16-RegisterAllocator` |
| basics/add-i32 | `AddI32-RegisterAllocator` |
| basics/sub-i16 | `SubI16-RegisterAllocator` |
| basics/many-args | `ManyArgs-RegisterAllocator` |
| control-flow/select (+ coalescing/redundant-copy) | `Select-RegisterAllocator` |
| control-flow/early-return | `EarlyReturn-RegisterAllocator` |
| pressure/live-across-call | `LiveAcrossCall-RegisterAllocator` |
| pressure/many-calls *(simplified to 2 calls)* | `ChainedCalls-RegisterAllocator` |
| pressure/many-calls *(genuine spill: 10 i16 live across a call)* | `CrossCallSpill-RegisterAllocator` |
| control-flow/if-else *(`a<b ? a+b : a-b` — see note)* | `IfElse-RegisterAllocator` |
| control-flow/if-else *(exact abs-diff `a>b ? a-b : b-a`)* | `IfElseAbsDiff-RegisterAllocator` |
| control-flow/loop-counter *(constant-delta loop — see note)* | `LoopCounter-RegisterAllocator` |
| realistic/fibonacci | `Fibonacci-RegisterAllocator` |
| memory/global-rw *(simplified: constant delta)* | `GlobalIncr-RegisterAllocator` |
| widths/truncate | `TruncI32ToI16-RegisterAllocator` |

### Equivalent / duplicate (collapse to a converted or existing test)

| Corpus case | Equivalent to |
|-------------|---------------|
| coalescing/copy-passthrough | `IdentityI8` (collapses to a passthrough in SSA) |
| coalescing/commutative | `AddI16` (identical MIR) |
| coalescing/swap | `SubI16` straight-line; the interesting swap-*cycle* is the existing `PhiElimination-SwapCycle` test |

### Skipped — blocker, by reason

| Reason | Corpus cases |
|--------|--------------|
| **No `arith` multiply** | basics/chain-arith, control-flow/nested-loop, pressure/pressure-high, memory/struct-pass, realistic/factorial-recursive |
| **No divide/modulo** | realistic/gcd |
| **No shift op** | constraints/shift-const, constraints/shift-var |
| **No bitwise and/or/xor** | constraints/bitops, constraints/compare-flags (`&`), pressure/pressure-i16 (`^`), realistic/crc8 (shift+`^`) |
| **i64 not supported** (+ `^`) | pressure/pressure-i64 |
| **i8 arithmetic not selectable** (only i16/i32 legalize to byte carry chains; bare `arith.addi i8` has no selection rule) | basics/add-i8, constraints/inc-dec |
| **`cast.zext`/`cast.sext` to i32 not legalized** | widths/zero-extend, widths/sign-extend, widths/mixed-widths (also i8 arith) |
| **`mem.load`/`mem.store` only accept `mem.symbol` addresses** (no arbitrary i16 pointers / frame slots) | memory/ptr-deref, memory/array-index, control-flow/loop-sum-array, realistic/strlen, memory/two-pointer-copy |
| **multi-way control flow (switch)** — the cmpi+cond_br fusion handles a single two-way branch; an N-way switch lowering is not yet implemented. | control-flow/switch *(multi-way)* |
| **aggregate / sret ABI not modelled** | basics/stack-args (also stack-arg ABI), memory/struct-return, memory/struct-return-sret |

**Phase 4 of the register-allocator redesign (spilling + rematerialization)
unblocked the control-flow / loop / pressure cases.** Branchy diamonds, counted
loops, the iterative-Fibonacci copy-cycle-in-a-loop, and a genuine cross-call
spill all allocate now where the old allocator aborted with "no free physical
register". Notably, on this target *almost none of them actually spill* — the
large zero-page register file absorbs the cross-block / back-edge pressure, so
only `CrossCallSpill` (which keeps more bytes live across a call than the
callee-saved zp budget) takes the store/reload path. The remaining skipped cases
are blocked by isel/legalizer/ABI gaps above, **not** by the allocator.

## Other findings surfaced during conversion

- **`cf.cond_br` carrying block-args on its edges leaves stray vregs after RA.**
  *(Fixed — register-allocator redesign Phase 0.)* A conditional branch written
  as `cf.cond_br %p, bb1(%a), bb2(%b)` used to produce post-RA output still
  referencing virtual registers (e.g. `mos6502.bne bb1(%10, %11)`) because
  PhiElimination inserted the phi-copies before the *shared* terminator. The pass
  now splits the multi-successor edge: each conditional edge carrying args gets a
  fresh single-successor block holding the `pseudo.copy` + `cf.br`, so no
  block-args survive. See `PhiElimination-CondBrArgs{OneEdge,BothEdges}.irie`.
  The converted `Select`/`EarlyReturn` tests still use arg-less `cond_br` with
  direct cross-block uses.
- **i16 compares emit two `cmp`/branch pairs** (one per byte) — that is correct
  multi-byte comparison lowering, not a bug, but it makes branchy tests longer
  than their llvm-mos counterparts.
