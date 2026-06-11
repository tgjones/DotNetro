// Euclid's algorithm: a loop with a modulo (itself a helper call or inline
// division) and a value swap each iteration. Combines call/clobber effects,
// loop-carried state, and a copy cycle.
int gcd(int a, int b) {
    while (b != 0) {
        int t = b;
        b = a % b;
        a = t;
    }
    return a;
}
