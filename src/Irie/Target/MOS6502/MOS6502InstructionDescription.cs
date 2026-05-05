using Irie.CodeGen;

namespace Irie.Target.MOS6502;

public sealed record MOS6502InstructionDescription(
    int            Opcode,
    string         Mnemonic,
    AddressingMode Mode,
    int            Size,
    int[]?         OperandClasses = null,
    int[]?         TiedOperands   = null,
    int[]?         ImplicitDefs   = null,
    int[]?         ImplicitUses   = null)
    : TargetInstructionDescription(Opcode, Mnemonic, Size, OperandClasses, TiedOperands, ImplicitDefs, ImplicitUses);
