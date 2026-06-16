// Ten i16 args (20 bytes) overflow the register-passing convention. The first
// few arrive in $a/$x/$rc*, the rest are passed on the soft stack and must be
// loaded back via fixed-stack address materialization (AddrLostk/AddrHistk).
// Exercises the ABI/allocation boundary that many-args (6 args, all in regs)
// never reaches.
int stack_args(int a, int b, int c, int d, int e,
               int f, int g, int h, int i, int j) {
    return a + b + c + d + e + f + g + h + i + j;
}
