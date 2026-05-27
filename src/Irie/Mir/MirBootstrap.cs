using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Dialects.Pseudo;

namespace Irie.Mir;

// Registers the four non-target dialects (core, arith, cf, pseudo) into
// DialectRegistry. Idempotent — safe to call multiple times. The Mir parser
// and writer both call this on first use, so most callers never need to.
//
// Target dialects (e.g. mos6502) register themselves from their target's
// constructor and are not handled here.
public static class MirBootstrap
{
    private static bool _registered;
    private static readonly object _lock = new();

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (_lock)
        {
            if (_registered) return;
            DialectRegistry.Register(new CoreDialect());
            DialectRegistry.Register(new ArithDialect());
            DialectRegistry.Register(new CfDialect());
            DialectRegistry.Register(new PseudoDialect());
            _registered = true;
        }
    }
}
