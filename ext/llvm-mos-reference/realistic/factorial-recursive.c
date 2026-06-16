// Recursion: n must survive the recursive self-call (caller-saved spill), and
// the result is multiplied on the way back. Tests spilling around a call plus a
// non-trivial multiply, in the smallest function that needs both.
int factorial(int n) {
    if (n <= 1) return 1;
    return n * factorial(n - 1);
}
