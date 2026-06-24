using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target;

// Port of llvm's constrainSelectedInstRegOperands / constrainOperandRegClass
// (llvm/lib/CodeGen/GlobalISel/Utils.cpp) and constrainGenericRegister
// (llvm/lib/CodeGen/RegisterBankInfo.cpp).
//
// After a target instruction is selected, each of its register operands must
// satisfy that operand's declared register class (DialectInstructionInfo.
// OperandClasses). This narrows the operand's vreg toward the required class,
// inserting a `pseudo.copy` split only when the vreg is already committed to an
// incompatible class — the getCommonSubClass-empty case. Call it on each target
// op the instruction selector emits, the same way each llvm-mos select() rule
// ends with constrainSelectedInstRegOperands.
//
// The two cases mirror constrainGenericRegister exactly:
//   * vreg has only a TYPE (TypedVReg, the "bank only" state) — set its class to
//     the operand class directly, no copy. (RegisterBankInfo.cpp:139-146)
//   * vreg already has a CLASS (ClassedVReg) — intersect with the operand class
//     (TryGetCommonSubClass); narrow in place if non-empty, else split with a
//     copy. (RegisterBankInfo.cpp:134-137 → MachineRegisterInfo::constrainRegClass)
//
// Because the vreg's class is mutated in place, processing instructions in
// selection order reproduces llvm's order-dependent split: the first operand to
// require an incompatible class pins the vreg, and the second triggers the copy.
public static class RegisterClassConstraining
{
    public static void ConstrainSelectedInstRegOperands(
        MirInstruction instr,
        MirFunction function,
        TargetRegisterInfo registerInfo,
        MirBuilder builder)
    {
        var info = DialectRegistry.ById(instr.Opcode.Dialect)
            .GetInstructionInfo(instr.Opcode.Code);
        var classes = info.OperandClasses;
        if (classes == null) return;

        var operands = instr.Operands;
        for (var i = 0; i < operands.Length && i < classes.Length; i++)
        {
            var req = classes[i];
            if (req == 0) continue;
            if (operands[i] is not VirtualReg v) continue;

            var reqName = registerInfo.GetRegisterClassName(req) ?? $"class{req}";
            var annotation = function.GetVRegAnnotation(v.Id);

            // Bank-only (typed, no committed class): set the class directly.
            if (annotation is not ClassedVReg classed)
            {
                function.ReclassifyVirtualRegister(v.Id, req, reqName);
                continue;
            }

            // Already classed: intersect with the operand's required class.
            var sub = registerInfo.TryGetCommonSubClass(classed.ClassId, req);
            if (sub == classed.ClassId) continue;          // already satisfies it

            if (sub is { } subClass)
            {
                // Non-empty intersection: narrow in place (e.g. any8 → ac).
                var subName = registerInfo.GetRegisterClassName(subClass) ?? $"class{subClass}";
                function.ReclassifyVirtualRegister(v.Id, subClass, subName);
                continue;
            }

            // Disjoint: split with a pseudo.copy so each role keeps its own class.
            var fresh = function.CreateVirtualRegisterInClass(req, reqName);
            if (!v.IsDefinition)
            {
                // Use: insert `fresh = copy v` before the instruction.
                builder.SetInsertionPointBefore(instr);
                builder.BuildInstruction(
                    PseudoDialect.OpRef(PseudoOp.Copy),
                    new VirtualReg(fresh, IsDefinition: true),
                    new VirtualReg(v.Id, IsDefinition: false));
                operands[i] = new VirtualReg(fresh, IsDefinition: false);
            }
            else
            {
                // Def: insert `v = copy fresh` after the instruction.
                builder.SetInsertionPointAfter(instr);
                builder.BuildInstruction(
                    PseudoDialect.OpRef(PseudoOp.Copy),
                    new VirtualReg(v.Id, IsDefinition: true),
                    new VirtualReg(fresh, IsDefinition: false));
                operands[i] = new VirtualReg(fresh, IsDefinition: true);
            }
        }
    }
}
