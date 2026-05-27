using Irie.Mir;

namespace DotNetro.Compiler.Tests.Mir;

public class MirBinaryRoundTripTests
{
    // Parses `input` from text, writes to binary, reads it back, writes to
    // text, and asserts the textual representation is preserved bit-for-bit.
    // The text round-trip is already covered by MirRoundTripTests; this
    // exercises the binary format on the same inputs to confirm the two
    // formats agree.
    private static async Task AssertBinaryRoundTrips(string input)
    {
        var original = MirModule.Parse(new StringReader(input));

        using var stream = new MemoryStream();
        using (var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            original.Write(bw);

        stream.Position = 0;
        MirModule decoded;
        using (var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            decoded = MirModule.Read(br);

        var sw = new StringWriter();
        decoded.Write(sw);
        await Assert.That(sw.ToString()).IsEqualTo(input);
    }

    [Test]
    public async Task RoundTrip_CoreReturnAndArithAdd()
    {
        var input = """
            func @Add : (i16, i16) -> i16 {
            bb0(%0 : i16, %1 : i16):
                %2 : i16 = arith.addi %0, %1
                core.return %2
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertBinaryRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_ArithSubAndAddIWithCarry()
    {
        var input = """
            func @SubAndCarry : (i8, i8, i1) -> void {
            bb0(%0 : i8, %1 : i8, %2 : i1):
                %3 : i8 = arith.subi %0, %1
                %4 : i8, %5 : i1 = arith.addi_with_carry %0, %1, %2
                core.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertBinaryRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_CfBranchAndCondBrWithBlockArgs()
    {
        // Exercises forward block references — bb2 is referenced from bb0
        // before bb2's header has been read.
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

        await AssertBinaryRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_PseudoCopyMergeUnmergeReturn()
    {
        var input = """
            func @PseudoOps : (i8, i8) -> i16 {
            bb0(%0 : i8, %1 : i8):
                %2 : i8 = pseudo.copy %0
                %3 : i16 = pseudo.merge %2, %1
                %4 : i8, %5 : i8 = pseudo.unmerge %3
                pseudo.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertBinaryRoundTrips(input);
    }

    [Test]
    public async Task RoundTrip_PhysRegsAndLiveinsAndImmediate()
    {
        // Numeric physregs (the parser only accepts numeric `$N` without a
        // target wired in), an entry-block liveins list, an immediate
        // operand, and a void-returning function.
        var input = """
            func @WithPhysRegs : (i8) -> void {
            bb0():
                [liveins: $0, $1]
                $0 = pseudo.copy 42
                pseudo.return
            }
            """.ReplaceLineEndings("\n") + "\n";

        await AssertBinaryRoundTrips(input);
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

        await AssertBinaryRoundTrips(input);
    }

    [Test]
    public async Task Read_RejectsBadMagic()
    {
        using var stream = new MemoryStream([0, 1, 2, 3, 1, 0, 0, 0]);
        using var br = new BinaryReader(stream);
        await Assert.That(() => MirModule.Read(br))
            .Throws<InvalidDataException>()
            .WithMessageContaining("magic");
    }

    [Test]
    public async Task Read_RejectsUnsupportedVersion()
    {
        using var stream = new MemoryStream();
        using (var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(new byte[] { (byte)'I', (byte)'R', (byte)'I', (byte)'3' });
            bw.Write(999);
        }

        stream.Position = 0;
        using var br = new BinaryReader(stream);
        await Assert.That(() => MirModule.Read(br))
            .Throws<InvalidDataException>()
            .WithMessageContaining("version");
    }
}
