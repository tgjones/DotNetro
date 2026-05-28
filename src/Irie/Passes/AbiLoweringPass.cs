using Irie.Dialects.Core;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Passes;

// Lowers the calling convention into target-agnostic pseudo ops:
//   - Entry-block typed parameters become live-ins + `pseudo.copy` from
//     physregs (plus a `pseudo.merge` reassembling each multi-byte parameter
//     back into the original parameter vreg).
//   - Each `core.return %v` becomes per-byte `pseudo.copy` to result physregs
//     (preceded by `pseudo.unmerge` for multi-byte return values), followed
//     by `pseudo.return`.
//
// The function signature (ParameterTypes / ReturnType) is preserved.
// Target-specific decisions (which physregs hold which bytes) live in the
// supplied CallLowering.
public sealed class AbiLoweringPass(Irie.Target.CallLowering callLowering) : MirFunctionPass
{
    public override string Name => "AbiLowering";

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);

        LowerFormalArguments(function, builder);
        LowerReturns(function, builder);
    }

    private void LowerFormalArguments(MirFunction function, MirBuilder builder)
    {
        var entryBlock = function.Blocks[0];
        var originalParameters = entryBlock.Parameters.ToArray();
        entryBlock.Parameters.Clear();

        builder.SetInsertionPointAtStart(entryBlock);
        callLowering.LowerFormalArguments(function, entryBlock, originalParameters, builder);
    }

    private void LowerReturns(MirFunction function, MirBuilder builder)
    {
        foreach (var block in function.Blocks)
        {
            if (block.Instructions.Count == 0) continue;

            var terminator = block.Instructions[^1];
            if (terminator.Opcode.Dialect != CoreDialect.Id) continue;
            if ((CoreOp)terminator.Opcode.Code != CoreOp.Return) continue;

            int? returnValueVreg = null;
            if (terminator.Operands.Length > 0
                && terminator.Operands[0] is VirtualReg v && !v.IsDefinition)
            {
                returnValueVreg = v.Id;
            }

            builder.SetInsertionPointBefore(terminator);
            callLowering.LowerReturn(function.ReturnType, returnValueVreg, builder);
            builder.BuildInstruction(PseudoDialect.OpRef(PseudoOp.Return));
            builder.Remove(terminator);
        }
    }
}
