using Irie.MachineCode;

namespace Irie.Target.MOS6502;

// Two-pass encoder: pass 1 (here) walks the module assigning absolute
// addresses to functions, local labels, and instructions; pass 2 (not yet
// implemented) will use the layout tables below to emit the actual 6502
// byte stream. See notes/mir-to-binary-plan.md §2.1.
public sealed class MOS6502BinaryEncoder
{
    // Function name → absolute start address. Resolves ExternalRef in pass 2.
    private Dictionary<string, int> _functionAddrs = new();

    // Per-function local label name → absolute address. Resolves LabelRef in pass 2.
    private Dictionary<MachineCodeFunction, Dictionary<string, int>> _localLabelAddrs = new();

    // The instructions to encode in pass 2, in declaration order, each with the
    // address it was laid out at and the function it belongs to (for label resolution).
    private List<LayoutEntry> _layoutEntries = new();

    public byte[] Encode(MachineCodeModule module, int origin)
    {
        var layout = LayoutOnly(module, origin);
        _functionAddrs = layout.FunctionAddrs;
        _localLabelAddrs = layout.LocalLabelAddrs;
        _layoutEntries = layout.Entries;

        throw new NotImplementedException(
            "MOS6502BinaryEncoder pass 2 (encode) is not yet implemented — see step 3 of notes/mir-to-binary-plan.md.");
    }

    // Runs pass 1 in isolation and returns the resolved layout. Encode() calls
    // this internally; tests use it to observe the layout state until pass 2 lands.
    public LayoutResult LayoutOnly(MachineCodeModule module, int origin)
    {
        var functionAddrs = new Dictionary<string, int>();
        var localLabelAddrs = new Dictionary<MachineCodeFunction, Dictionary<string, int>>();
        var entries = new List<LayoutEntry>();

        var cursor = origin;

        foreach (var function in module.Functions)
        {
            if (!functionAddrs.TryAdd(function.Name, cursor))
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: duplicate function name '{function.Name}'.");

            var labelMap = new Dictionary<string, int>();
            localLabelAddrs[function] = labelMap;

            foreach (var entry in function.Body)
            {
                switch (entry)
                {
                    case MachineCodeLabel label:
                        // Labels are zero-width: they pin the cursor without consuming bytes.
                        if (!labelMap.TryAdd(label.Name, cursor))
                            throw new InvalidOperationException(
                                $"MOS6502BinaryEncoder: duplicate label '{label.Name}' in function '{function.Name}'.");
                        break;

                    case MachineCodeInstruction instr:
                        entries.Add(new LayoutEntry(cursor, function, instr));
                        cursor += InstructionLength(MOS6502InstructionInfo.Instance.Get(instr.Opcode).Mode);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"MOS6502BinaryEncoder: unhandled MachineCodeEntry {entry.GetType().Name}.");
                }
            }
        }

        return new LayoutResult(functionAddrs, localLabelAddrs, entries);
    }

    private static int InstructionLength(AddressingMode mode) => mode switch
    {
        AddressingMode.Implied   => 1,
        AddressingMode.Immediate => 2,
        AddressingMode.ZeroPage  => 2,
        AddressingMode.ZeroPageX => 2,
        AddressingMode.ZeroPageY => 2,
        AddressingMode.IndirectX => 2,
        AddressingMode.IndirectY => 2,
        AddressingMode.Relative  => 2,
        AddressingMode.Absolute  => 3,
        AddressingMode.AbsoluteX => 3,
        AddressingMode.AbsoluteY => 3,
        AddressingMode.Indirect  => 3,
        _ => throw new InvalidOperationException(
            $"MOS6502BinaryEncoder: cannot compute instruction length for addressing mode {mode}."),
    };

    public sealed record LayoutEntry(int Addr, MachineCodeFunction Parent, MachineCodeInstruction Instruction);

    public sealed record LayoutResult(
        Dictionary<string, int> FunctionAddrs,
        Dictionary<MachineCodeFunction, Dictionary<string, int>> LocalLabelAddrs,
        List<LayoutEntry> Entries);
}
