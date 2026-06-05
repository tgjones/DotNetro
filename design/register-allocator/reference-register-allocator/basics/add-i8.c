// Single-byte add. Two-address ADC on $a: one operand must be coalesced into
// the accumulator, the carry flag is set up with CLC/LDImm1 0.
char add_i8(char a, char b) {
    return a + b;
}
