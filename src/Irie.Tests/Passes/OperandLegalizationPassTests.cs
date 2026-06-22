using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// Validates OperandLegalizationPass: it inserts a class-crossing pseudo.copy
// exactly where a use operand's required register class is DISJOINT from its
// source vreg's def class, and is a no-op everywhere else. These reproduce the
// greedy-RA "gap 2" shape (an `ac` result feeding an `imag8`/zp adc addend)
// WITHOUT the isel out-funnels, the situation the pass exists to legalize.
public sealed class OperandLegalizationPassTests
{
    // Constructing the target registers the MOS6502 dialect and publishes
    // MOS6502Dialect.Id, so MOS6502Dialect.OpRef resolves to the real dialect
    // (not the default Id=0 "core"). An EXPLICIT static constructor removes the
    // type's beforefieldinit flag, guaranteeing these run before the first static
    // member access (NewFunction / AddAdc), i.e. before any OpRef call.
    private static readonly MOS6502Target Target;
    private static readonly OperandLegalizationPass Pass;

    static OperandLegalizationPassTests()
    {
        Target = new MOS6502Target();
        Pass = new OperandLegalizationPass(Target.RegisterInfo);
    }

    private static MirFunction NewFunction(string name) =>
        new(name, [], IRType.Void);

    // mos6502.adc operand block: def[0]=Ac result, def[1]=Cc carry_out,
    // use[0]=Ac L (tied), use[1]=Imag8 R, use[2]=Cc carry_in.
    private static MirInstruction AddAdc(
        MirBlock bb, int result, int carryOut, int l, int r, int carryIn) =>
        bb.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Adc),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(carryOut, IsDefinition: true),
            new VirtualReg(l, IsDefinition: false),
            new VirtualReg(r, IsDefinition: false),
            new VirtualReg(carryIn, IsDefinition: false));

    private static bool IsCopy(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;

    // GAP-2 SHAPE: an `ac`-classed value (here defined by a first adc) is used at
    // the `imag8` addend position (use[1]) of a second adc. Ac ∩ Imag8 = ∅, so
    // the pass must insert exactly one pseudo.copy before the second adc, its def
    // a fresh Imag8 vreg, and rewrite the operand to read that temp.
    [Test]
    public async Task AcValueAtImag8Operand_InsertsOneCopyInImag8()
    {
        var fn = NewFunction("ac_to_imag8");
        var bb = fn.CreateBlock();

        // The Ac-classed value we will misuse as an Imag8 addend.
        var acValue = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var carry0  = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var accL    = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var addend0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Imag8, "zp");
        var cin0    = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        // First adc defines acValue (an `ac` result).
        AddAdc(bb, acValue, carry0, accL, addend0, cin0);

        var result1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var carry1  = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var accL1   = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var cin1    = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        // Second adc uses acValue as its Imag8 addend (use[1]) — the disjoint use.
        var adc1 = AddAdc(bb, result1, carry1, accL1, acValue, cin1);

        Pass.Run(fn);

        // Exactly one copy inserted, immediately before the second adc.
        var copies = bb.Instructions.Where(IsCopy).ToList();
        await Assert.That(copies.Count).IsEqualTo(1);

        var copy = copies[0];
        var copyIdx = bb.Instructions.IndexOf(copy);
        var adc1Idx = bb.Instructions.IndexOf(adc1);
        await Assert.That(copyIdx).IsEqualTo(adc1Idx - 1);

        // The copy's def is a fresh vreg annotated Imag8, and it copies acValue.
        var tempDef = (VirtualReg)copy.Operands[0];
        await Assert.That(tempDef.IsDefinition).IsTrue();
        var tempAnnot = (ClassedVReg)fn.GetVRegAnnotation(tempDef.Id);
        await Assert.That(tempAnnot.ClassId).IsEqualTo(MOS6502RegisterClass.Imag8);
        var copySrc = (VirtualReg)copy.Operands[1];
        await Assert.That(copySrc.Id).IsEqualTo(acValue);

        // The adc's addend operand (use[1] = operand index 3) now reads the temp.
        var addendUse = (VirtualReg)adc1.Operands[3];
        await Assert.That(addendUse.Id).IsEqualTo(tempDef.Id);
    }

    // NEGATIVE: an Anyi8 (flexible) value feeding the same Imag8 addend operand.
    // Anyi8 ⊇ Imag8, so the classes intersect — NO copy is inserted, and the
    // operand is left untouched. (This is what the isel out-funnels currently
    // achieve, hence the pass is a no-op on the live pipeline.)
    [Test]
    public async Task Anyi8ValueAtImag8Operand_InsertsNoCopy()
    {
        var fn = NewFunction("any8_to_imag8");
        var bb = fn.CreateBlock();

        var any8   = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var carry  = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var accL   = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var addend = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Imag8, "zp");
        var cin    = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var result = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        // any8 used at the Imag8 addend position.
        var adc = AddAdc(bb, result, carry, accL, any8, cin);

        Pass.Run(fn);

        // The only instruction is the adc — no copy inserted.
        await Assert.That(bb.Instructions.Count).IsEqualTo(1);
        var addendUse = (VirtualReg)adc.Operands[3];
        await Assert.That(addendUse.Id).IsEqualTo(any8);
    }

    // NEGATIVE: an Ac value feeding an Ac operand (use[0], the tied accumulator).
    // Same class — NO copy, operand untouched.
    [Test]
    public async Task AcValueAtAcOperand_InsertsNoCopy()
    {
        var fn = NewFunction("ac_to_ac");
        var bb = fn.CreateBlock();

        var acValue = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");

        var result = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
        var carry  = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        var addend = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Imag8, "zp");
        var cin    = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Cc, "cc");
        // acValue used at use[0] (Ac, tied accumulator).
        var adc = AddAdc(bb, result, carry, acValue, addend, cin);

        Pass.Run(fn);

        // The only instruction is the adc — no copy inserted.
        await Assert.That(bb.Instructions.Count).IsEqualTo(1);
        var accUse = (VirtualReg)adc.Operands[2];
        await Assert.That(accUse.Id).IsEqualTo(acValue);
    }
}
