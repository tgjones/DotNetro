#!/usr/bin/env bash
#
# Regenerates the whole-pipeline llvm-mos artifacts for every .c case here.
#
# Pipeline per case, in a single clang invocation:
#   foo.c --clang -Os -S -mllvm -print-changed-->  foo.s   (final assembly)
#                                            `-->  foo.txt  (print-changed dump)
#
# foo.s is the *final* assembly — including the prologue/epilogue inserted by
# Prologue/Epilogue Insertion (callee-saved $rc save/restore), which the old
# -stop-after=virtregrewriter .mir snapshot did not show.
#
# foo.txt is the -print-changed dump: the IR / Machine IR after every pass that
# *changed* it (passes that made no change collapse to a one-line
# "omitted because no change" marker, so you still see the full pass order).
# This is ~4x smaller than -print-after-all. Use it to see *which* pass produced
# a given sequence. It stops before assembly emission, which is why foo.s is a
# separate file.
#
# Override the toolchain or opt level via env vars, e.g.:
#   CLANG=/path/to/clang OPT=-O2 ./build.sh
set -euo pipefail

CLANG=${CLANG:-/Users/timjones/Code/llvm-mos/build/bin/clang}
OPT=${OPT:--Os}

cd "$(dirname "$0")"

count=0
find . -name '*.c' | sort | while read -r c; do
  base="${c%.c}"
  "$CLANG" --target=mos "$OPT" -S -mllvm -print-changed "$c" -o "$base.s" 2>"$base.txt"
  echo "built ${base#./}"
  count=$((count + 1))
done
