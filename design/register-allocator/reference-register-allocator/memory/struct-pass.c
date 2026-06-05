// A small struct passed by value is spread across several convention registers.
// The allocator sees the individual fields as separate live byte-values that
// must be gathered to compute the result.
struct point { int x; int y; };
int struct_pass(struct point p) {
    return p.x * p.y;
}
