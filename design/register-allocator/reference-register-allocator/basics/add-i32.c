// 32-bit add: a four-byte carry chain. More live byte-values than physical
// registers, so some operands sit in the imaginary zp register file ($rc*).
long add_i32(long a, long b) {
    return a + b;
}
