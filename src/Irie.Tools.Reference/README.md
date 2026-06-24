# irie-report

Compares Irie's MOS6502 codegen against the llvm-mos reference corpus. Two
subcommands:

```bash
# Build iriec + this tool, then:

# Score the whole corpus → markdown scoreboard:
dotnet run --project src/Irie.Tools.Reference -- generate-report
# → writes doc/irie/llvm-mos-comparison.md, prints the aggregate to stdout

# Diff one case's pipeline, stage by stage → HTML artifact:
dotnet run --project src/Irie.Tools.Reference -- generate-diff ext/llvm-mos-reference/basics/add-i8.c
# → writes artifacts/irie-diff/basics-add-i8.html (uncommitted)
```

## `generate-report`

Scores Irie's MOS6502 codegen against the whole corpus and writes a markdown
scoreboard. The generated
[`doc/irie/llvm-mos-comparison.md`](../../doc/irie/llvm-mos-comparison.md) is
checked in to git — regenerate it after codegen changes and commit the diff.

For each case in [`ext/llvm-mos-reference/`](../../ext/llvm-mos-reference) it pairs
the committed llvm-mos final assembly (`.s`) with the matching Irie test (same
basename) under
[`Lit/CodeGen/MOS6502/LlvmMosReference/`](../Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference),
runs `iriec --target mos6502 --emit=asm` on the test, counts **real instructions**
on each side (skipping directives, labels, comments), and reports:

- a **headline aggregate** — total Irie vs total llvm-mos instructions, as a ratio
  and percentage, over the paired cases;
- a **table** — per-case llvm-mos count, Irie count, Δ, ratio, and status
  (`converted` / `blocked` / `error`), sorted worst-first;
- **collapsible per-case assembly** (`<details>`) — the llvm-mos and Irie listings
  for each case.

Options (all optional):

| Flag | Default |
|------|---------|
| `--corpus` | `ext/llvm-mos-reference` |
| `--tests`  | `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference` |
| `--iriec`  | the `iriec` built alongside this tool |
| `--out`    | `doc/irie/llvm-mos-comparison.md` |

Instruction count is the headline metric — apples-to-apples without assembling
llvm-mos. Every `.irie` pairing is a **faithful mirror** of its corpus `.c` —
adapting the input to dodge an Irie limitation is not allowed (see the suite
README's *Fidelity rule*). A few deltas are still apples-to-oranges, but only
because of *output*-level optimizations Irie's pipeline does not perform (e.g.
loop strength reduction in `loop-counter`) — never because the input was
simplified; see the suite README's pairing caveat.

## `generate-diff`

Lays one case's two pipelines side by side, stage by stage, as a self-contained
HTML artifact (written under `artifacts/` — gitignored). The left column is the
committed llvm-mos `-print-changed` IR dump (`<case>.txt`); the right column is
the Irie MIR captured from `iriec --print-changed`, one panel per pass.

Stages are aligned at the shared GlobalISel-style backbone (start, ABI/arg
lowering, legalization, instruction selection, PHI elimination, two-address,
register allocation, copy cleanup, pseudo expansion). Stages with no counterpart
on one side — llvm's LLVM-IR-level optimization and analysis passes, Irie's
target-specific passes — render single-sided, and that's expected. Passes that
ran without changing the IR are shown dimmed.

Pass the `.c`, `.txt`, or any sibling of a corpus case as the argument; the
`.txt` (llvm dump) and `.c` (source) are derived. Options:

| Flag | Default |
|------|---------|
| `--tests` | `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference` |
| `--iriec` | the `iriec` built alongside this tool |
| `--out`   | `artifacts/irie-diff/<category>-<name>.html` |

See [`ext/llvm-mos-reference/README.md`](../../ext/llvm-mos-reference/README.md)
for the improvement workflow this scoreboard drives.
