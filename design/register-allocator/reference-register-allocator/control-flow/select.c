// A ternary that often lowers to a branchless select / conditional move rather
// than a diamond. Tests how the allocator handles a value with two reaching
// definitions that are both live into a single use.
int select_val(int cond, int a, int b) {
    return cond ? a : b;
}
