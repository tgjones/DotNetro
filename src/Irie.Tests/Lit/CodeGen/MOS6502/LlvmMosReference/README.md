# LlvmMosReference — full-pipeline Irie tests vs the llvm-mos corpus

These `.irie` lit tests are the Irie side of the reference corpus in
[`ext/llvm-mos-reference/`](../../../../../../ext/llvm-mos-reference). That corpus
is C → llvm-mos *final assembly* (what a mature compiler produces); these tests
pin what **Irie's whole pipeline** produces for the same computation, so we can
measure and close the gap. The [`irie-report`](../../../../../Irie.Tools.Reference)
tool pairs each test here with the matching corpus case by **basename** and
reports the per-case and aggregate instruction-count delta.

## Convention

Each test:
- `; RUN: @iriec --target mos6502 --emit=asm @file` — the **full** pipeline, all
  the way to final MOS6502 assembly (the comparison point against the corpus
  `.s`).
- a header naming the corpus case (`; Corpus: llvm-mos-reference/<cat>/<case>`)
  and the behaviour of note;
- a `; CHECK:` block pinning Irie's **current** `--emit=asm` output. These are
  characterization / golden tests — they document today's behaviour, warts and
  all, so codegen changes show up as a reviewable diff. CHECK lines are an
  ordered subsequence (DotLit matches each after the previous), generated
  mechanically (run iriec, regex-escape each output line) — not hand-written;
- the input MIR: pre-pipeline generic-dialect MIR (`arith.*`, `cf.*`, `mem.*`,
  `call.func`, `core.return`).

> vregs in the input MIR must be **numeric** (`%0`, `%1`, …) — the MIR lexer
> rejects named vregs.

Filenames match the corpus basename (`add-i16.irie` ↔
`ext/llvm-mos-reference/basics/add-i16.{c,s,txt}`) so `irie-report` can pair them.

## Converted (14 tests)

| Corpus case | Test |
|-------------|------|
| basics/ret-const | `ret-const` |
| basics/identity-i8 | `identity-i8` |
| basics/add-i16 | `add-i16` |
| basics/add-i32 | `add-i32` |
| basics/sub-i16 | `sub-i16` |
| basics/many-args | `many-args` |
| control-flow/select (+ coalescing/redundant-copy) | `select` |
| control-flow/early-return | `early-return` |
| control-flow/if-else (exact abs-diff) | `if-else` |
| control-flow/loop-counter | `loop-counter` |
| pressure/live-across-call | `live-across-call` |
| realistic/fibonacci | `fibonacci` |
| memory/global-rw (constant delta) | `global-rw` |
| widths/truncate | `truncate` |

### Equivalent / duplicate (collapse to a converted test)
- coalescing/copy-passthrough ≈ `identity-i8` (collapses to a passthrough in SSA)
- coalescing/commutative ≈ `add-i16` (identical MIR)
- coalescing/swap ≈ `sub-i16` straight-line

## Blocked — by reason

These corpus cases cannot yet be expressed in Irie's generic MIR *and* survive
the full pipeline to asm. The blockers are **Irie's current limitations**; this
doubles as a punch-list (and `irie-report` flags each as uncovered):

| Reason | Corpus cases |
|--------|--------------|
| **Spill lowering not wired to `--emit=asm`** — `pseudo.spill`/`pseudo.reload` survive to `PseudoExpansionPass`, which throws. (RA *does* spill; the static-stack placement that lowers the spill pseudos is not yet on the asm path.) | pressure/many-calls *(genuine spill)* |
| **No `arith` multiply** | basics/chain-arith, control-flow/nested-loop, pressure/pressure-high, memory/struct-pass, realistic/factorial-recursive |
| **No divide/modulo** | realistic/gcd |
| **No shift op** | constraints/shift-const, constraints/shift-var |
| **No bitwise and/or/xor** | constraints/bitops, constraints/compare-flags, pressure/pressure-i16, realistic/crc8 |
| **i64 not supported** | pressure/pressure-i64 |
| **i8 arithmetic not selectable** (only i16/i32 legalize to byte carry chains) | basics/add-i8, constraints/inc-dec |
| **`cast.zext`/`cast.sext` to i32 not legalized** | widths/zero-extend, widths/sign-extend, widths/mixed-widths |
| **`mem.load`/`mem.store` only accept `mem.symbol` addresses** (no arbitrary i16 pointers / frame slots) | memory/ptr-deref, memory/array-index, control-flow/loop-sum-array, realistic/strlen, memory/two-pointer-copy |
| **multi-way control flow (switch)** — only a single two-way cmp+branch fusion exists | control-flow/switch |
| **aggregate / sret ABI not modelled** | basics/stack-args, memory/struct-return, memory/struct-return-sret |

### Pairing caveat

`irie-report` pairs by basename. All tests are now faithful translations of the
corpus C, but two have caveats:

- **`loop-counter`** is faithful (`s += i`), but llvm-mos strength-reduces the
  whole loop to a closed-form multiply (`__mulsi3`) — an optimisation Irie's
  pipeline does not perform — so the comparison is apples-to-oranges.
- **`if-else`** is an *equivalent* rephrasing (`a>b` → `a<b` with operands
  swapped), since `arith.cmpi` lacks `sgt`/`ugt`. Same computation.

The straight-line `basics/*` cases are the cleanest apples-to-apples comparisons.

## Reading the divergences

- **i16 compares emit two `cmp`/branch pairs** (one per byte) — correct
  multi-byte comparison lowering, not a bug, but it makes branchy tests longer
  than their llvm-mos counterparts.
- **Register preference**: Irie often parks short cross-instruction values in the
  zero-page register file where llvm-mos uses a real GPR (`$y`). Visible as extra
  zero-page traffic vs the corpus `.s`.

To close a gap, see the *Improvement workflow* in
[`ext/llvm-mos-reference/README.md`](../../../../../../ext/llvm-mos-reference/README.md):
sort `irie-report` by worst ratio, read the corpus `.s` + `.txt` and the
responsible llvm-mos pass, improve the Irie pass, regenerate the CHECK block, and
watch the headline ratio move.
