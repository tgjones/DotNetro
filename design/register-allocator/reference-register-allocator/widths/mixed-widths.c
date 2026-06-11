// i8, i16 and i32 values interacting in one expression. Live ranges of
// different byte-widths overlap, so the allocator manages register-class sizes
// that change as values are extended and truncated through the computation.
long mixed_widths(char a, int b, long c) {
    int t = a + b;
    long u = t + c;
    return u + a;
}
