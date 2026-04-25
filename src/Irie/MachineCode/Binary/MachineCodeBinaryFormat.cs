namespace Irie.MachineCode.Binary;

internal static class MachineCodeBinaryFormat
{
    internal enum EntryKindTag : byte { Label, Instruction }
    internal enum OperandKindTag : byte { Register, Immediate, LabelRef, ExternalRef }
}
