using Irie.MachineCode;

namespace Irie.Target.MOS6502;

// Two-pass encoder: pass 1 walks the module assigning absolute addresses to
// functions, local labels, and instructions; pass 2 uses the layout tables to
// emit the actual 6502 byte stream. See notes/mir-to-binary-plan.md §2.1.
public sealed class MOS6502BinaryEncoder
{
    // Function name → absolute start address. Resolves ExternalRef in pass 2.
    private Dictionary<string, int> _functionAddrs = new();

    // Global symbol name → absolute start address. Shares the symbol namespace
    // with _functionAddrs but kept separate so duplicate-function-name checks
    // still report function-vs-function clashes specifically.
    private Dictionary<string, int> _globalAddrs = new();

    // Per-function local label name → absolute address. Resolves LabelRef in pass 2.
    private Dictionary<MachineCodeFunction, Dictionary<string, int>> _localLabelAddrs = new();

    // The instructions to encode in pass 2, in declaration order, each with the
    // address it was laid out at and the function it belongs to (for label resolution).
    private List<LayoutEntry> _layoutEntries = new();

    // The globals to encode in pass 2, in declaration order, each with the
    // address it was laid out at.
    private List<GlobalLayoutEntry> _globalLayoutEntries = new();

    // Relative branches whose target is out of signed-8-bit reach and must be
    // relaxed into `Bxx_inverse *+5; JMP target` (5 bytes instead of 2).
    // Determined by iterating layout to a fixpoint (relaxation only grows code).
    private HashSet<MachineCodeInstruction> _relaxedBranches = new();

    public byte[] Encode(MachineCodeModule module, int origin)
    {
        // Iterate layout until the set of out-of-range branches stabilises.
        // Each relaxed branch grows by 3 bytes, which can push other branches
        // out of range, so we repeat until no new branch needs relaxing.
        var relaxed = new HashSet<MachineCodeInstruction>();
        LayoutResult layout;
        while (true)
        {
            layout = LayoutOnly(module, origin, relaxed);
            var grew = false;
            foreach (var entry in layout.Entries)
            {
                if (MOS6502InstructionInfo.Instance.Get(entry.Instruction.Opcode).Mode != AddressingMode.Relative)
                    continue;
                if (relaxed.Contains(entry.Instruction)) continue;

                var target = ResolveLocalLabelIn(layout.LocalLabelAddrs, entry.Parent,
                    ((MachineCodeOperand.LabelRef)entry.Instruction.Operands[0]).Name, entry.Addr);
                var offset = target - (entry.Addr + 2);
                if (offset < -128 || offset > 127)
                {
                    relaxed.Add(entry.Instruction);
                    grew = true;
                }
            }
            if (!grew) break;
        }

        _relaxedBranches = relaxed;
        _functionAddrs = layout.FunctionAddrs;
        _globalAddrs = layout.GlobalAddrs;
        _localLabelAddrs = layout.LocalLabelAddrs;
        _layoutEntries = layout.Entries;
        _globalLayoutEntries = layout.GlobalEntries;

        // Total length spans from origin to the cursor after the last laid-out
        // instruction or global. If the module is empty we emit an empty byte stream.
        var totalLength = 0;
        foreach (var entry in _layoutEntries)
        {
            var end = entry.Addr - origin + EntryLength(entry.Instruction);
            if (end > totalLength) totalLength = end;
        }
        foreach (var globalEntry in _globalLayoutEntries)
        {
            var end = globalEntry.Addr - origin + globalEntry.Global.SizeInBytes;
            if (end > totalLength) totalLength = end;
        }

        var bytes = new byte[totalLength];

        foreach (var (addr, parent, instr) in _layoutEntries)
        {
            var bytePos = addr - origin;

            var mode = MOS6502InstructionInfo.Instance.Get(instr.Opcode).Mode;

            // Relaxed branch: emit `Bxx_inverse *+5; JMP target` (5 bytes). The
            // inverse branch skips the 3-byte absolute JMP that reaches the far
            // target. The original short branch's condition is inverted.
            if (mode == AddressingMode.Relative && _relaxedBranches.Contains(instr))
            {
                var labelRef = (MachineCodeOperand.LabelRef)instr.Operands[0];
                var targetAddr = ResolveLocalLabel(parent, labelRef.Name, addr);

                WriteByte(bytes, bytePos, InverseBranchOpcode(instr.Opcode));
                WriteByte(bytes, bytePos + 1, 3); // skip the JMP (3 bytes)
                WriteByte(bytes, bytePos + 2, MOS6502Opcode.JMP_Absolute);
                WriteLE16(bytes, bytePos + 3, targetAddr);
                continue;
            }

            WriteByte(bytes, bytePos, instr.Opcode);
            switch (mode)
            {
                case AddressingMode.Implied:
                    break;

                case AddressingMode.Immediate:
                case AddressingMode.ZeroPage:
                case AddressingMode.ZeroPageX:
                case AddressingMode.ZeroPageY:
                case AddressingMode.IndirectX:
                case AddressingMode.IndirectY:
                    EncodeOneByteOperand(bytes, bytePos, addr, instr, mode);
                    break;

                case AddressingMode.Relative:
                    EncodeRelativeOperand(bytes, bytePos, addr, parent, instr);
                    break;

                case AddressingMode.Absolute:
                case AddressingMode.AbsoluteX:
                case AddressingMode.AbsoluteY:
                case AddressingMode.Indirect:
                    EncodeTwoByteOperand(bytes, bytePos, addr, parent, instr, mode);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"MOS6502BinaryEncoder: cannot encode addressing mode {mode} for opcode ${instr.Opcode:X2} at ${addr:X4}.");
            }
        }

        foreach (var globalEntry in _globalLayoutEntries)
            EncodeGlobal(bytes, origin, globalEntry);

        return bytes;
    }

    private void EncodeGlobal(byte[] bytes, int origin, GlobalLayoutEntry entry)
    {
        var (addr, global) = entry;

        // Zero-init: byte[] default-init already wrote zeros, nothing to do.
        if (global.Items is null) return;

        var cursor = addr - origin;
        var end = cursor + global.SizeInBytes;

        foreach (var item in global.Items)
        {
            switch (item)
            {
                case MachineCodeDataBytes(var data):
                    if (cursor + data.Length > end)
                        throw new InvalidOperationException(
                            $"MOS6502BinaryEncoder: data items in global '{global.SymbolName}' exceed its declared size of {global.SizeInBytes} bytes.");
                    Array.Copy(data, 0, bytes, cursor, data.Length);
                    cursor += data.Length;
                    break;

                case MachineCodeDataSymbolRef(var name):
                    if (cursor + 2 > end)
                        throw new InvalidOperationException(
                            $"MOS6502BinaryEncoder: data items in global '{global.SymbolName}' exceed its declared size of {global.SizeInBytes} bytes.");
                    WriteLE16(bytes, cursor, ResolveSymbol(name, addr));
                    cursor += 2;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"MOS6502BinaryEncoder: unknown data item kind {item.GetType().Name} in global '{global.SymbolName}'.");
            }
        }
        // Remaining bytes between cursor and end stay zero (default byte[] init).
    }

    private void EncodeOneByteOperand(byte[] bytes, int bytePos, int addr, MachineCodeInstruction instr, AddressingMode mode)
    {
        var operand = instr.Operands[0];
        switch (operand)
        {
            case MachineCodeOperand.Immediate imm:
                WriteByte(bytes, bytePos + 1, (int)(imm.Value & 0xFF));
                break;

            case MachineCodeOperand.ExternalRef ext when ext.Half == SymbolHalf.LowByte:
                WriteByte(bytes, bytePos + 1, ResolveSymbol(ext.Name, addr) & 0xFF);
                break;

            case MachineCodeOperand.ExternalRef ext when ext.Half == SymbolHalf.HighByte:
                WriteByte(bytes, bytePos + 1, (ResolveSymbol(ext.Name, addr) >> 8) & 0xFF);
                break;

            default:
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: opcode ${instr.Opcode:X2} ({mode}) at ${addr:X4} has unsupported operand kind {operand.GetType().Name}.");
        }
    }

    private void EncodeRelativeOperand(byte[] bytes, int bytePos, int addr, MachineCodeFunction parent, MachineCodeInstruction instr)
    {
        var operand = instr.Operands[0];
        if (operand is not MachineCodeOperand.LabelRef labelRef)
            throw new InvalidOperationException(
                $"MOS6502BinaryEncoder: relative branch opcode ${instr.Opcode:X2} at ${addr:X4} requires a LabelRef operand but got {operand.GetType().Name}.");

        var targetAddr = ResolveLocalLabel(parent, labelRef.Name, addr);
        var offset = targetAddr - (addr + 2);
        if (offset < -128 || offset > 127)
            throw new InvalidOperationException(
                $"MOS6502BinaryEncoder: branch out of range for opcode ${instr.Opcode:X2} at ${addr:X4} → '{labelRef.Name}' (offset {offset}).");

        WriteByte(bytes, bytePos + 1, (byte)(sbyte)offset);
    }

    private void EncodeTwoByteOperand(byte[] bytes, int bytePos, int addr, MachineCodeFunction parent, MachineCodeInstruction instr, AddressingMode mode)
    {
        var operand = instr.Operands[0];
        switch (operand)
        {
            case MachineCodeOperand.Immediate imm:
                WriteLE16(bytes, bytePos + 1, (int)(imm.Value & 0xFFFF));
                break;

            case MachineCodeOperand.LabelRef labelRef:
                WriteLE16(bytes, bytePos + 1, ResolveLocalLabel(parent, labelRef.Name, addr));
                break;

            case MachineCodeOperand.ExternalRef ext when ext.Half == SymbolHalf.Full:
                WriteLE16(bytes, bytePos + 1, ResolveSymbol(ext.Name, addr));
                break;

            case MachineCodeOperand.ExternalRef ext:
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: opcode ${instr.Opcode:X2} ({mode}) at ${addr:X4} cannot use SymbolHalf.{ext.Half} — low/high halves only make sense in Immediate mode.");

            default:
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: opcode ${instr.Opcode:X2} ({mode}) at ${addr:X4} has unsupported operand kind {operand.GetType().Name}.");
        }
    }

    private int ResolveLocalLabel(MachineCodeFunction parent, string name, int referringAddr)
    {
        if (!_localLabelAddrs.TryGetValue(parent, out var labelMap) || !labelMap.TryGetValue(name, out var addr))
            throw new InvalidOperationException(
                $"MOS6502BinaryEncoder: undefined local label '{name}' referenced from function '{parent.Name}' at ${referringAddr:X4}.");
        return addr;
    }

    private int ResolveSymbol(string name, int referringAddr)
    {
        if (_functionAddrs.TryGetValue(name, out var addr)) return addr;
        if (_globalAddrs.TryGetValue(name, out addr))       return addr;
        throw new InvalidOperationException(
            $"MOS6502BinaryEncoder: undefined symbol '{name}' referenced from instruction at ${referringAddr:X4}.");
    }

    private static void WriteByte(byte[] bytes, int pos, int value)
    {
        bytes[pos] = (byte)(value & 0xFF);
    }

    private static void WriteLE16(byte[] bytes, int pos, int value)
    {
        bytes[pos]     = (byte)(value & 0xFF);
        bytes[pos + 1] = (byte)((value >> 8) & 0xFF);
    }

    // Runs pass 1 in isolation and returns the resolved layout. Encode() calls
    // this internally; tests use it to observe the layout state until pass 2 lands.
    public LayoutResult LayoutOnly(MachineCodeModule module, int origin)
        => LayoutOnly(module, origin, new HashSet<MachineCodeInstruction>());

    private LayoutResult LayoutOnly(MachineCodeModule module, int origin, HashSet<MachineCodeInstruction> relaxed)
    {
        var functionAddrs = new Dictionary<string, int>();
        var globalAddrs = new Dictionary<string, int>();
        var localLabelAddrs = new Dictionary<MachineCodeFunction, Dictionary<string, int>>();
        var entries = new List<LayoutEntry>();
        var globalEntries = new List<GlobalLayoutEntry>();

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
                        cursor += relaxed.Contains(instr)
                            ? 5 // Bxx_inverse (2) + JMP abs (3)
                            : InstructionLength(MOS6502InstructionInfo.Instance.Get(instr.Opcode).Mode);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"MOS6502BinaryEncoder: unhandled MachineCodeEntry {entry.GetType().Name}.");
                }
            }
        }

        foreach (var global in module.Globals)
        {
            if (functionAddrs.ContainsKey(global.SymbolName))
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: global '{global.SymbolName}' clashes with function of the same name.");
            if (!globalAddrs.TryAdd(global.SymbolName, cursor))
                throw new InvalidOperationException(
                    $"MOS6502BinaryEncoder: duplicate global name '{global.SymbolName}'.");

            globalEntries.Add(new GlobalLayoutEntry(cursor, global));
            cursor += global.SizeInBytes;
        }

        return new LayoutResult(functionAddrs, globalAddrs, localLabelAddrs, entries, globalEntries);
    }

    private int EntryLength(MachineCodeInstruction instr)
    {
        var mode = MOS6502InstructionInfo.Instance.Get(instr.Opcode).Mode;
        if (mode == AddressingMode.Relative && _relaxedBranches.Contains(instr))
            return 5;
        return InstructionLength(mode);
    }

    // The conditional-branch opcode that branches on the opposite condition,
    // used when relaxing an out-of-range branch into a branch-over-JMP.
    private static int InverseBranchOpcode(int opcode) => opcode switch
    {
        MOS6502Opcode.BEQ => MOS6502Opcode.BNE,
        MOS6502Opcode.BNE => MOS6502Opcode.BEQ,
        MOS6502Opcode.BCC => MOS6502Opcode.BCS,
        MOS6502Opcode.BCS => MOS6502Opcode.BCC,
        MOS6502Opcode.BMI => MOS6502Opcode.BPL,
        MOS6502Opcode.BPL => MOS6502Opcode.BMI,
        MOS6502Opcode.BVC => MOS6502Opcode.BVS,
        MOS6502Opcode.BVS => MOS6502Opcode.BVC,
        _ => throw new InvalidOperationException(
            $"MOS6502BinaryEncoder: no inverse branch for opcode ${opcode:X2} (cannot relax)."),
    };

    private static int ResolveLocalLabelIn(
        Dictionary<MachineCodeFunction, Dictionary<string, int>> localLabelAddrs,
        MachineCodeFunction parent, string name, int referringAddr)
    {
        if (!localLabelAddrs.TryGetValue(parent, out var labelMap) || !labelMap.TryGetValue(name, out var addr))
            throw new InvalidOperationException(
                $"MOS6502BinaryEncoder: undefined local label '{name}' referenced from function '{parent.Name}' at ${referringAddr:X4}.");
        return addr;
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

    public sealed record GlobalLayoutEntry(int Addr, MachineCodeGlobal Global);

    public sealed record LayoutResult(
        Dictionary<string, int> FunctionAddrs,
        Dictionary<string, int> GlobalAddrs,
        Dictionary<MachineCodeFunction, Dictionary<string, int>> LocalLabelAddrs,
        List<LayoutEntry> Entries,
        List<GlobalLayoutEntry> GlobalEntries);
}
