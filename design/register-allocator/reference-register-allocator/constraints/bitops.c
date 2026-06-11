// AND/ORA/EOR are two-address ops on the accumulator. A chain of them forces
// repeated coalescing into $a while keeping the other operands live in zp.
int bitops(int a, int b, int c) {
    return (a & b) | (b ^ c) | (a & c);
}
