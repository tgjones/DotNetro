// A simple diamond. Both arms compute into a value that merges via a phi at the
// join block; the allocator must agree on a register for the merged value
// across both predecessors (cross-block liveness + phi coalescing).
int if_else(int a, int b) {
    int r;
    if (a > b) {
        r = a - b;
    } else {
        r = b - a;
    }
    return r;
}
