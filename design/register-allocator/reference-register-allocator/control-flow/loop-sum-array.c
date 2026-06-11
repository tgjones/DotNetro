// Sum an array in a loop. Loop-carried state is the pointer/index plus the
// accumulator, and each iteration also needs a scratch register for the load.
// Tests register stability across the back edge under mild pressure.
int loop_sum_array(const int *a, int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += a[i];
    }
    return s;
}
