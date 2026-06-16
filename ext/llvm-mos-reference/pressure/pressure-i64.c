// 64-bit values are 8 bytes each; just a few live at once blows past the
// register file. The longest carry chains plus the heaviest spill traffic.
long long pressure_i64(long long a, long long b, long long c) {
    long long t0 = a + b;
    long long t1 = b + c;
    long long t2 = a + c;
    return (t0 ^ t1) + (t1 ^ t2) + (t0 ^ t2);
}
