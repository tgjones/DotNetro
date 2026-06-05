// Six i16 args consume all the convention live-in registers ($a, $x, then
// $rc2..). Forces the allocator to juggle many simultaneously-live in-values.
int many_args(int a, int b, int c, int d, int e, int f) {
    return a + b + c + d + e + f;
}
