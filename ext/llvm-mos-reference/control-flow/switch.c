// A switch lowers to a chain of compares or a jump table, creating multiple
// blocks that all merge to a single return phi. Tests cross-block allocation
// over a wider, shallower CFG than a simple diamond.
int switch_val(int x) {
    switch (x) {
        case 0:  return 10;
        case 1:  return 20;
        case 2:  return 30;
        case 3:  return 40;
        default: return -1;
    }
}
