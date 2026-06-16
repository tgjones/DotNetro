// A struct too large to return in registers is returned via a hidden 'sret'
// pointer: the signature becomes void(ptr sret, ...) and the fields are written
// through that pointer pair with STIndirIdx. Contrast with struct-return, where
// the small result fits in the return registers. Tests pointer-pair allocation
// for the implicit result-address argument.
struct big { int a, b, c, d, e; };
struct big make_big(int x) {
    struct big r = { x, x + 1, x + 2, x + 3, x + 4 };
    return r;
}
