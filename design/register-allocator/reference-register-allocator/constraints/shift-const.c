// Shifts by a constant amount lower to ASL/LSR sequences operating on $a (or
// in-place on zp). Tests allocation around an op family pinned to the
// accumulator and the carry flag.
int shift_const(int a) {
    return (a << 3) | (a >> 2);
}
