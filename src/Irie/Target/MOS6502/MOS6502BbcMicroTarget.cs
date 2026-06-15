namespace Irie.Target.MOS6502;

// BBC Micro subtarget: identical to MOS6502Target in every codegen aspect,
// but defaults --origin to $2000 and packages --emit=bin output as a DFS .ssd
// disk image instead of returning raw bytes.
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000;

    // $70–$8F is the BBC Micro user zero page (safe even with BASIC resident),
    // dedicated here to static frame storage — a separate namespace from the RC
    // imaginary register file. 32 bytes; non-reentrant frames that fit promote
    // to direct zero-page access (StaticFramePlacementPass).
    public override FrameZeroPageWindow FreeZeroPage => new(0x70, 0x20);

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(code, loadAddress: origin, execAddress: origin, fileName: "MAIN");
}
