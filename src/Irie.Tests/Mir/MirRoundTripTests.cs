using Irie.Mir;

namespace DotNetro.Compiler.Tests.Mir;

public class MirRoundTripTests
{
    // Parses `input`, writes the parsed module, and asserts the text round-trips
    // unchanged. Whitespace must match exactly — the writer's canonical layout
    // is part of the contract.
    private static async Task AssertRoundTrips(string input)
    {
        var module = MirModule.Parse(new StringReader(input));
        var sw = new StringWriter();
        module.Write(sw);
        await Assert.That(sw.ToString()).IsEqualTo(input);
    }

    [Test]
    public async Task RoundTrip_CoreReturnAndArithAdd()
    {
        // Covers: function header with non-empty signature, block parameters
        // with typed-vreg annotation, arith.addi, core.return (with a value).
        var input = """
            func @Add : (i16, i16) -> i16 {
            bb0(%0 : i16, %1 : i16):
                %2 : i16 = arith.addi %0, %1
                core.return %2
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_ArithSubAndAddIWithCarry()
    {
        // Covers: arith.subi, multi-def arith.addi_with_carry (`%r : i8, %cout : i1 = ...`),
        // void return type, core.return with no value.
        var input = """
            func @SubAndCarry : (i8, i8, i1) -> void {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8 = arith.subi %0, %1
                %4 : i8, %5 : i1 = arith.addi_with_carry %0, %1, %2
                core.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_CfBranchAndCondBrWithBlockArgs()
    {
        // Covers: cf.br, cf.cond_br, block-target with args `bb2(%0)`,
        // a forward block reference, an empty-parameter block header `bb2():`.
        var input = """
            func @Branches : (i1, i8) -> i8 {
            bb0(%0 : i1, %1 : i8):
                cf.cond_br %0, bb1, bb2(%1)
            bb1():
                cf.br bb2(%1)
            bb2(%2 : i8):
                core.return %2
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_PseudoCopyMergeUnmergeReturn()
    {
        // Covers: pseudo.copy (vreg-to-vreg), pseudo.merge, pseudo.unmerge
        // (multi-def), pseudo.return (terminator, no operands).
        var input = """
            func @PseudoOps : (i8, i8) -> i16 {
            bb0(%0 : i8, %1 : i8):
                %2 : i8 = pseudo.copy %0
                %3 : i16 = pseudo.merge %2, %1
                %4 : i8, %5 : i8 = pseudo.unmerge %3
                pseudo.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_LineComments_ParsedAndDropped()
    {
        // Comments (`;` to end-of-line) are accepted by the parser but never
        // emitted by the writer. The round-tripped output should be the same
        // module body without the comment lines.
        var input = """
            ; this comment is dropped
            func @Add : (i32, i32) -> i32 {
            bb0(%0 : i32, %1 : i32):
                ; defs first
                %2 : i32 = arith.addi %0, %1  ; trailing comment
                core.return %2
            }
            """.ReplaceLineEndings("\n");

        var expected = """
            func @Add : (i32, i32) -> i32 {
            bb0(%0 : i32, %1 : i32):
                %2 : i32 = arith.addi %0, %1
                core.return %2
            }
            """.ReplaceLineEndings("\n") + "\n";

        var module = MirModule.Parse(new StringReader(input));
        var sw = new StringWriter();
        module.Write(sw);
        await Assert.That(sw.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task RoundTrip_TwoFunctionsSeparatedByBlankLine()
    {
        var input = """
            func @F : (i8) -> i8 {
            bb0(%0 : i8):
                core.return %0
            }

            func @G : () -> void {
            bb0():
                core.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertRoundTrips(input);
    }
}
