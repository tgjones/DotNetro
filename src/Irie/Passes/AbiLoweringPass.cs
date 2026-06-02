using Irie.Dialects.Call;
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
//   - Each `call.func` is replaced by per-arg `pseudo.copy → physreg` setups,
//     a target-specific call instruction (e.g. `mos6502.jsr.abs`), and
//     per-return `pseudo.copy ← physreg` teardowns.
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
        LowerCalls(function, builder);
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

    private void LowerCalls(MirFunction function, MirBuilder builder)
    {
        foreach (var block in function.Blocks)
        {
            // Snapshot the instruction list so we can safely mutate it.
            foreach (var instr in block.Instructions.ToList())
            {
                if (instr.Parent == null) continue;
                if (instr.Opcode.Dialect != CallDialect.Id) continue;
                if ((CallOp)instr.Opcode.Code != CallOp.Func) continue;

                LowerCallFunc(instr, function, builder);
            }
        }
    }

    private void LowerCallFunc(MirInstruction call, MirFunction function, MirBuilder builder)
    {
        // Operand layout: defs[0..N-1] (return-value vregs), uses[0]=Symbol,
        // uses[1..M] (arg vregs).
        var returnVregs = new List<int>();
        var returnTypes = new List<IRType>();
        string? calleeName = null;
        var argVregs = new List<int>();
        var argTypes = new List<IRType>();

        foreach (var op in call.Operands)
        {
            switch (op)
            {
                case VirtualReg v when v.IsDefinition:
                    returnVregs.Add(v.Id);
                    returnTypes.Add(GetVregType(function, v.Id));
                    break;
                case Symbol s when calleeName == null:
                    calleeName = s.Name;
                    break;
                case VirtualReg v when !v.IsDefinition:
                    argVregs.Add(v.Id);
                    argTypes.Add(GetVregType(function, v.Id));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"AbiLowering: call.func has unexpected operand kind {op.GetType().Name}.");
            }
        }

        if (calleeName == null)
            throw new InvalidOperationException(
                "AbiLowering: call.func is missing the callee Symbol operand.");

        builder.SetInsertionPointBefore(call);
        callLowering.LowerCall(
            calleeName,
            argTypes.ToArray(),
            argVregs.ToArray(),
            returnTypes.ToArray(),
            returnVregs.ToArray(),
            builder);
        builder.Remove(call);
    }

    private static IRType GetVregType(MirFunction function, int vreg)
    {
        if (function.GetVRegAnnotation(vreg) is TypedVReg typed)
            return typed.Type;
        throw new InvalidOperationException(
            $"AbiLowering: call.func vreg %{vreg} has non-typed annotation.");
    }
}
