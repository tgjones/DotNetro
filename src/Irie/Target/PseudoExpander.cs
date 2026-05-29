using Irie.Mir;

namespace Irie.Target;

// Target hook for the final pipeline pass, PseudoExpansionPass. The pass walks
// every remaining `pseudo.copy` (the only pseudo op left after RA + isel +
// addressing-mode selection) and asks the target to lower it to real target
// instructions based on the concrete source/destination physregs.
//
// Per unified-IR plan §5.6 / §6: the table of (src kind, dst kind) → target ops
// is wholly target-specific. The generic pass provides only the iteration and
// the insertion-point bookkeeping.
public abstract class PseudoExpander
{
    // Lower a single `pseudo.copy` instruction. The builder's insertion point
    // is set immediately before `copy` when this is called; the pass removes
    // `copy` once Expand returns. Implementations emit one or more target
    // instructions through the builder.
    public abstract void Expand(MirInstruction copy, MirBuilder builder);
}
