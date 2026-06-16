// Iterative Fibonacci: a loop carrying a pair of values that are rotated each
// iteration (a, b = b, a+b). The rotation is a copy cycle inside a loop, mixing
// coalescing pressure with back-edge liveness -- a compact but rich RA case.
int fibonacci(int n) {
    int a = 0, b = 1;
    for (int i = 0; i < n; i++) {
        int t = a + b;
        a = b;
        b = t;
    }
    return a;
}
