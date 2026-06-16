# irie-report

Scores Irie's MOS6502 codegen against the llvm-mos reference corpus and writes an
HTML scoreboard.

```bash
# Build iriec + this tool, then:
dotnet run --project src/Irie.Tools.Reference
# → writes ext/llvm-mos-reference/report.html, prints the aggregate to stdout
```

For each case in [`ext/llvm-mos-reference/`](../../ext/llvm-mos-reference) it pairs
the committed llvm-mos final assembly (`.s`) with the matching Irie test (same
basename) under
[`Lit/CodeGen/MOS6502/LlvmMosReference/`](../Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference),
runs `iriec --target mos6502 --emit=asm` on the test, counts **real instructions**
on each side (skipping directives, labels, comments), and reports:

- a **headline aggregate** — total Irie vs total llvm-mos instructions, as a ratio
  and percentage, over the paired cases;
- a **sortable table** — per-case llvm-mos count, Irie count, Δ, ratio, and status
  (`converted` / `blocked` / `error`); rows colour-graded by ratio (green ≈ parity
  → red worse), sorted worst-first;
- **collapsible side-by-side assembly** per case (click a row).

Options (all optional):

| Flag | Default |
|------|---------|
| `--corpus` | `ext/llvm-mos-reference` |
| `--tests`  | `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference` |
| `--iriec`  | the `iriec` built alongside this tool |
| `--out`    | `<corpus>/report.html` |

`report.html` is gitignored (it's a generated artifact). Instruction count is the
headline metric — apples-to-apples without assembling llvm-mos. Some converted
cases use *adapted* inputs (see the suite README's pairing caveat), so read their
delta as indicative, not exact.

See [`ext/llvm-mos-reference/README.md`](../../ext/llvm-mos-reference/README.md)
for the improvement workflow this scoreboard drives.
