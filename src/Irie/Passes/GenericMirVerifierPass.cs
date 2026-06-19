using Irie.Mir;

namespace Irie.Passes;

// Pre-pipeline verifier for generic-dialect MIR. Enforces the invariant that an
// integer *value* is never an inline `Immediate` operand — it is always an
// `arith.constant` def. Inline `Immediate`s are reserved for structural
// attributes (the cmpi predicate, mem offsets/counts, frame-slot indices, …)
// and for `arith.constant`'s own value. Each dialect declares its legal
// immediate operand positions via Dialect.IsLegalImmediateOperand.
//
// Mutates nothing; throws MirVerificationException on the first violation so a
// bad inline literal surfaces as a clean diagnostic rather than an InvalidCast
// crash deeper in isel/lowering. Runs first, before ReturnMergePass.
public sealed class GenericMirVerifierPass : MirFunctionPass
{
    public override string Name => "GenericMirVerifier";

    // The invariant only applies to the generic (non-target) dialects — the ones
    // MirBootstrap registers. Target dialects (e.g. mos6502) legitimately carry
    // inline immediates (e.g. `mos6502.lda.imm #$00`), so the verifier ignores
    // any instruction whose dialect is not in this set. The set is keyed by the
    // dialects' stable prefixes rather than IDs (IDs are assigned at registration
    // time, which differs depending on which targets have been constructed).
    private static readonly HashSet<string> GenericDialectPrefixes =
        ["core", "arith", "cast", "cf", "call", "mem", "pseudo"];

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var dialect = DialectRegistry.ById(instr.Opcode.Dialect);

                // Only the generic dialects participate in the inline-immediate
                // invariant; target dialects carry legal inline immediates.
                if (!GenericDialectPrefixes.Contains(dialect.Prefix))
                    continue;

                // Defs always come first in the operand array; count the leading
                // register defs so we can map array indices to use indices.
                var defCount = 0;
                foreach (var operand in instr.Operands)
                {
                    if (operand is VirtualReg { IsDefinition: true }
                        or PhysicalReg { IsDefinition: true })
                        defCount++;
                    else
                        break;
                }

                for (var i = 0; i < instr.Operands.Length; i++)
                {
                    if (instr.Operands[i] is not Immediate imm) continue;

                    var useIndex = i - defCount;
                    if (dialect.IsLegalImmediateOperand(instr.Opcode.Code, useIndex))
                        continue;

                    var opName = $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";
                    throw new MirVerificationException(
                        $"MirVerifier: in @{function.Name}, {opName} has an inline immediate " +
                        $"value operand ({imm.Value}) at use {useIndex}. Value constants must be " +
                        $"an arith.constant def: `%c = arith.constant {imm.Value}` then " +
                        $"`arith.addi %x, %c`. Inline immediates are only allowed for structural " +
                        $"attributes (e.g. the cmpi predicate) and arith.constant's own value.");
                }
            }
        }
    }
}
