// High pressure with wide (i32) values and long live ranges: many products
// must stay live simultaneously, exceeding the register file and forcing
// genuine spills (STStk / LDStk to the soft stack).
long pressure_high(long a, long b, long c, long d) {
    long t0 = a * b;
    long t1 = b * c;
    long t2 = c * d;
    long t3 = d * a;
    long t4 = t0 + t1;
    long t5 = t2 + t3;
    long t6 = t4 * t5;
    return t6 + t0 + t1 + t2 + t3;
}
