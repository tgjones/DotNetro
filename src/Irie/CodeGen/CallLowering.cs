using Irie.IR;

namespace Irie.CodeGen;

public abstract class CallLowering
{
    // Called once at the start of translation to materialize formal arguments
    // as physical-register copies into the function's virtual registers.
    // Implementations should populate valueMap with [IRArgument → vreg] entries.
    public abstract void LowerFormalArguments(
        IRFunction irFunction,
        MachineFunction machineFunction,
        MachineBasicBlock entryBlock,
        MachineIRBuilder builder,
        Dictionary<IRValue, int> valueMap);

    // Called at each return site to copy the return value into physical registers
    // and emit the target's return instruction.
    public abstract void LowerReturn(
        IRType irReturnType,
        int? returnValueVreg,
        MachineBasicBlock block,
        MachineIRBuilder builder);
}
