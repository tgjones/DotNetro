// Trivial passthrough: the single i8 arg arrives in $a and is also the i8
// return register, so an ideal allocator emits essentially no moves.
char identity_i8(char a) {
    return a;
}
