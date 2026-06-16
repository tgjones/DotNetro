namespace Irie.Target.MOS6502;

// BBC Micro subtarget: identical to MOS6502Target in every codegen aspect,
// but defaults --origin to $2000, promotes non-reentrant frames into the BBC
// Micro's free user zero page, and packages --emit=bin output as a DFS .ssd
// disk image instead of returning raw bytes.
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000;

    public override void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm)
    {
        base.AddPostRegisterAllocationPasses(pm);

        // The zero-page frame-placement pass is BBC-Micro-specific: $70–$8F is
        // the portable user zero page, which a bare 6502 has no equivalent of, so
        // the base chip never promotes (its slots stay absolute). It runs post-RA
        // and before the generic FrameAccessLoweringPass.
        pm.AddPass(new MOS6502FramePlacementPass());
    }

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(code, loadAddress: origin, execAddress: origin, fileName: "MAIN");
}
