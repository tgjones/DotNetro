// A counting loop with a loop-carried accumulator and induction variable. Both
// 's' and 'i' are live across the back edge, so their registers must be stable
// around the loop -- the canonical reason RA cares about loop structure.
int loop_counter(int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += i;
    }
    return s;
}
