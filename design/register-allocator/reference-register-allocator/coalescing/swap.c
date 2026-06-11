// A value swap creates a copy cycle (a<-b, b<-a). The allocator cannot coalesce
// both copies away; it must break the cycle, classically via a temp register.
int swap_diff(int a, int b) {
    int t = a;
    a = b;
    b = t;
    return a - b;
}
