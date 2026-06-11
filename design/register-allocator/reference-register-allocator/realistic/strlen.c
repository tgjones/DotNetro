// A classic pointer-walking loop. The pointer is loop-carried and lives in a zp
// pointer pair; each iteration loads a byte and tests it, then the length is
// derived from the pointer difference. Pointer-class liveness across a loop.
unsigned strlen_impl(const char *s) {
    const char *p = s;
    while (*p) {
        p++;
    }
    return (unsigned)(p - s);
}
