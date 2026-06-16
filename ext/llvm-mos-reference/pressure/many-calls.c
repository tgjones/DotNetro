// Four calls, with all four arguments needing to survive every call. Maximal
// caller-saved pressure: forces a cluster of STStk spills around the JSRs.
extern int g(int);
int many_calls(int a, int b, int c, int d) {
    int s = g(a) + g(b);
    int t = g(c) + g(d);
    return s + t + a + b + c + d;
}
