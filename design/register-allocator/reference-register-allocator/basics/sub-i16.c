// Subtraction is non-commutative, so the allocator cannot freely swap which
// operand lands in $a (unlike add). Tests operand-order-sensitive coalescing.
int sub_i16(int a, int b) {
    return a - b;
}
