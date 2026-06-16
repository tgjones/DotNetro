// Load through a pointer then store through it. The 16-bit pointer must live in
// a zero-page pointer pair for (zp),y addressing, while $a/$y carry the value
// and index -- tests allocation into the pointer register class.
int ptr_deref(int *p) {
    int v = *p;
    *p = v + 1;
    return v;
}
