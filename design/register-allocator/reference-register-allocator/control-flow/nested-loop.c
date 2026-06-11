// Nested loops keep two induction variables and an accumulator live across two
// back edges simultaneously. The inner loop reuses registers the outer loop
// needs to preserve -- a stress test for live ranges spanning loop nests.
int nested_loop(int n, int m) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        for (int j = 0; j < m; j++) {
            s += i * j;
        }
    }
    return s;
}
