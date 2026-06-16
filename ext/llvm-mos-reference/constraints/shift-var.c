// A variable shift amount forces a loop (shift-by-one, decrement count). The
// count and the value being shifted are both loop-carried, with the value
// pinned to the accumulator each iteration -- a constraint + liveness mix.
int shift_var(int a, int n) {
    return a << n;
}
