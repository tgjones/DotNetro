// Materialize a constant return value. No inputs live -> tests immediate
// loading into the return registers ($a/$x for i16).
int ret_const(void) {
    return 1234;
}
