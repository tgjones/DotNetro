// A chain of copies (a -> x -> y -> result). A good coalescer collapses the
// whole chain so the input register is reused directly as the output register.
int copy_passthrough(int a) {
    int x = a;
    int y = x;
    int z = y;
    return z;
}
