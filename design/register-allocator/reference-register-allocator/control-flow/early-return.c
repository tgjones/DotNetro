// Guard clauses with multiple early returns. Each exit is a separate block, and
// the arguments have live ranges that end at different points -- tests that the
// allocator frees registers promptly along short paths.
int early_return(int a, int b, int c) {
    if (a < 0) return 0;
    if (b < 0) return a;
    if (c < 0) return a + b;
    return a + b + c;
}
