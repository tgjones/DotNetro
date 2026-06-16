// Two independent values that are conditionally returned. The phi at the join
// wants both incoming values coalesced into the same return register; whether
// that succeeds depends on the copy-elimination quality.
int redundant_copy(int cond, int a, int b) {
    int r;
    if (cond) {
        r = a;
    } else {
        r = b;
    }
    return r;
}
