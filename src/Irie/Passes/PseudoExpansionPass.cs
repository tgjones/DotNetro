using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Final pass in the unified-MIR pipeline. Replaces every remaining `pseudo.copy`
// instruction with the appropriate target move(s) via the target-supplied
// PseudoExpander. After this pass the function contains only target opcodes:
// no `pseudo.*` or `core.*` survives.
//
// Earlier `pseudo.merge` / `pseudo.unmerge` artifacts were folded by the
// legalizer; `pseudo.return` was lowered to the target return op by isel; only
// `pseudo.copy` reaches this point. Any other surviving pseudo op is an
// upstream-pass bug and triggers an exception.
public sealed class PseudoExpansionPass(PseudoExpander expander) : MirFunctionPass
{
    public override string Name => "PseudoExpansion";

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions.ToList())
            {
                if (instr.Opcode.Dialect != PseudoDialect.Id) continue;

                var op = (PseudoOp)instr.Opcode.Code;
                if (op != PseudoOp.Copy)
                {
                    var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
                    throw new InvalidOperationException(
                        $"PseudoExpansionPass: unexpected `{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}` " +
                        $"survived to the final pass; expected only `pseudo.copy`.");
                }

                builder.SetInsertionPointBefore(instr);
                expander.Expand(instr, builder);
                builder.Remove(instr);
            }
        }
    }
}
