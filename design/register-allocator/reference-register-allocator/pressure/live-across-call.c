// 'a' must stay live across the call to g(), which clobbers the caller-saved
// registers. The allocator must spill or relocate 'a' to a preserved location.
extern int g(int);
int live_across_call(int a) {
    int r = g(a);
    return r + a;
}
