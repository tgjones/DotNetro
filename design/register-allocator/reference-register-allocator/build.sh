#!/usr/bin/env bash
#
# Regenerates the .ll (LLVM IR) and .mir (Machine IR, post-register-allocation)
# artifacts for every .c test case in this corpus.
#
# Pipeline per case:
#   foo.c  --clang -O2 -S -emit-llvm-->  foo.ll
#   foo.ll --llc  -stop-after=virtregrewriter-->  foo.mir
#
# We stop after virtregrewriter because llvm-mos uses a two-step register
# allocator: the Greedy Register Allocator assigns virtual registers to
# physical registers, then the Virtual Register Rewriter rewrites the MIR to
# use those physical registers. virtregrewriter is the first point at which the
# allocation is fully baked into the instruction stream (no vregs remain).
#
# Override the toolchain or opt level via env vars, e.g.:
#   CLANG=/path/to/clang LLC=/path/to/llc OPT=-Oz ./build.sh
set -euo pipefail

CLANG=${CLANG:-/Users/timjones/Code/llvm-mos/build/bin/clang}
LLC=${LLC:-/Users/timjones/Code/llvm-mos/build/bin/llc}
OPT=${OPT:--O2}

cd "$(dirname "$0")"

count=0
find . -name '*.c' | sort | while read -r c; do
  base="${c%.c}"
  "$CLANG" "$OPT" -S -emit-llvm "$c" -o "$base.ll"
  "$LLC" -stop-after=virtregrewriter "$base.ll" -o "$base.mir"
  echo "built ${base#./}"
  count=$((count + 1))
done
