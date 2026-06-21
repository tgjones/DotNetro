# irie-report

Scores Irie's MOS6502 codegen against the llvm-mos reference corpus and writes a
markdown scoreboard.

```bash
# Build iriec + this tool, then:
dotnet run --project src/Irie.Tools.Reference
# → writes doc/irie/llvm-mos-comparison.md, prints the aggregate to stdout
```

The generated [`doc/irie/llvm-mos-comparison.md`](../../doc/irie/llvm-mos-comparison.md)
is checked in to git — regenerate it after codegen changes and commit the diff.

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

See [`ext/llvm-mos-reference/README.md`](../../ext/llvm-mos-reference/README.md)
for the improvement workflow this scoreboard drives.
