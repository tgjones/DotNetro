using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Target-agnostic post-RA pass that rewrites every abstract frame access (a
// byte load/store still naming a FrameSlot + offset) into the concrete
// instruction sequence that addresses the slot. The llvm-mos PEI step that runs
// MOSRegisterInfo::eliminateFrameIndex over the function.
//
// The pass owns only the walk: it asks the target's FrameLowering which
// instructions are abstract frame accesses (FrameLowering.IsFrameAccess) and
// delegates each to FrameLowering.LowerFrameAccess, which reads the slot's
// placement (StackId / Offset) and emits the addressing sequence. All the
// target-specific addressing knowledge lives behind that hook — this pass never
// inspects an opcode or a stack id.
//
// Pipeline position: post-RA — after register allocation and the target's own
// post-RA passes (e.g. the placement pass that sets each slot's StackId, and the
// addressing-mode selector), before PrologueEpilogueInsertion / PseudoExpansion.
// Running after RA is mandatory for the same reason llvm-mos places it there:
// the value byte is already in its physreg and the access's scratch clobbers are
// already reserved, so the expansion needs no register scavenging.
public sealed class FrameAccessLoweringPass(FrameLowering frameLowering) : MirFunctionPass
{
    public override string Name => "FrameAccessLowering";

    private readonly FrameLowering _frameLowering = frameLowering;

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);

        foreach (var block in function.Blocks)
        {
            // LowerFrameAccess removes the access and inserts its replacement
            // before it, so snapshot the instruction list and re-find live
            // accesses by identity rather than index.
            var accesses = new List<MirInstruction>();
            foreach (var instr in block.Instructions)
                if (_frameLowering.IsFrameAccess(instr))
                    accesses.Add(instr);

            foreach (var access in accesses)
                _frameLowering.LowerFrameAccess(function, access, builder);
        }
    }
}
