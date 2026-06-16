// A short dependency chain (a*b + c - a) where 'a' is reused after the first
// op, so it must stay live across the multiply. Tests overlapping live ranges.
int chain_arith(int a, int b, int c) {
    return a * b + c - a;
}
