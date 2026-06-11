// Several comparisons feeding boolean logic. Each compare defines the flag
// registers ($n/$v/$z/$c) and must not have its operands clobbered before the
// flags are consumed -- tests flag-register liveness, which is never spilled.
char compare_flags(int a, int b, int c) {
    return (a < b) & (b < c) & (a != c);
}
