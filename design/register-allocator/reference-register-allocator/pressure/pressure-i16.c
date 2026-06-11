// Moderate pressure: several i16 temporaries live at once, but few enough to
// fit in the imaginary register file without spilling to the soft stack.
int pressure_i16(int a, int b, int c, int d) {
    int t0 = a + b;
    int t1 = c + d;
    int t2 = a + c;
    int t3 = b + d;
    return (t0 ^ t1) + (t2 ^ t3);
}
