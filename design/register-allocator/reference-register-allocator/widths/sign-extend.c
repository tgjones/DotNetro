// Sign-extend a signed char to int: the high byte is derived from the sign bit
// (a shift/branch sequence), producing an extra live value the allocator must
// place. Contrast with zero-extend, where the high byte is a constant.
int sign_extend(signed char a) {
    return (int)a - 1;
}
