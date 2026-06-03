namespace Irie.Target.MOS6502;

// BBC Micro subtarget: identical to MOS6502Target in every codegen aspect,
// but defaults --origin to $2000 and packages --emit=bin output as a DFS .ssd
// disk image instead of returning raw bytes.
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000;

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(code, loadAddress: origin, execAddress: origin, fileName: "MAIN");
}
