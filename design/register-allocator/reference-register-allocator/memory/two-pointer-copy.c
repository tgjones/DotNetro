// Two pointers live simultaneously across a loop, each needing its own 16-bit
// zero-page pointer pair ($rs1 and $rs2), plus an index register for the (zp),y
// addressing. Stresses pointer-pair allocation and X/Y index pressure together
// -- more demanding than strlen's single walking pointer.
void two_pointer_copy(int *dst, const int *src, int n) {
    for (int i = 0; i < n; i++) {
        dst[i] = src[i];
    }
}
