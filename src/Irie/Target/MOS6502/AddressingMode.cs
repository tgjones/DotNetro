namespace Irie.Target.MOS6502;

public enum AddressingMode
{
    Implied,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndirectX,
    IndirectY,
    Relative,

    // Pseudo opcodes that don't correspond to a real 6502 instruction (e.g. LDImm1
    // for materializing an i1 constant). Lowered to one or more real instructions
    // by a later pass when emitting MachineCode.
    Pseudo,
}
