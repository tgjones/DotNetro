// (a + b) where the result is needed but a and b are not reused. A commutative
// op lets the allocator pick either operand for the tied $a slot to minimize
// moves -- a coalescing degree of freedom subtraction would not offer.
int commutative(int a, int b) {
    int s = a + b;
    return s;
}
