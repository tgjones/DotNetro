// 16-bit add: a two-byte carry chain (ADCImag8 x2) threading the carry flag
// between the low and high bytes. Exercises the $a/$c interplay the most.
int add_i16(int a, int b) {
    return a + b;
}
