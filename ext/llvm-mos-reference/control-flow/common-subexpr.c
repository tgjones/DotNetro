// A diamond whose two arms share a common sub-expression (a + b) that is NOT the
// leading computation of either arm: each arm first computes a *different* value
// (d + 1 vs d - 1, stored to g0) and only then the shared a + b, which it stores
// to a *different* global (g1 vs g2). The else arm has an extra, asymmetric store
// (g3 = d) -- present purely to stop the tiny diamond from being flattened into
// branchless `select`s (speculation), so the conditional branch *survives* and
// the only way to compute a + b once is to genuinely hoist it above the branch.
//
// This exists to motivate generalizing Irie's HoistCommonCodePass. That pass is
// the local SimplifyCFG `hoistCommonCodeFromSuccessors` analogue, but a *narrow*
// version of it: it only matches when the *first* instruction of every successor
// is identical, then climbs sole-predecessor edges hoisting leading instructions.
// Here the arms' leading instructions differ (d + 1 vs d - 1), so the match fails
// immediately and the shared a + b -- sitting below the differing code -- is
// never hoisted; Irie would emit the multi-byte add twice.
//
// llvm-mos hoists a + b once, above the surviving branch (see the .s: the `adc`
// pair computing a + b appears before the `bmi`, and each arm then stores the
// already-computed value to its own global). LLVM's real
// `hoistCommonCodeFromSuccessors` is more general than ours -- it skips over the
// non-matching leading instructions and hoists the matching one below them --
// and its dedicated GVNHoist pass (llvm/lib/Transforms/Scalar/GVNHoist.cpp)
// generalizes further to any common dominator. Either is the direction
// HoistCommonCodePass is structured to grow toward.
int g0, g1, g2, g3;
void common_subexpr(int a, int b, int c, int d) {
    if (c < 0) { g0 = d + 1; g1 = a + b; }
    else       { g0 = d - 1; g2 = a + b; g3 = d; }
}
