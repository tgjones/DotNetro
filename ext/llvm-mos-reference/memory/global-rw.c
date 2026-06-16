// Read-modify-write of a global uses absolute addressing, not a register-held
// pointer. The value passes through $a; tests that no register is wasted
// holding the (statically known) address.
int counter;
int global_rw(int delta) {
    counter += delta;
    return counter;
}
