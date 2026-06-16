using Irie.MachineCode;

namespace Irie.Target.MOS6502;

public static class MOS6502AssemblyParser
{
    private static readonly Dictionary<(string Mnemonic, AddressingMode Mode), int> _reverseTable = BuildReverseTable();

    public static MachineCodeModule Parse(TextReader reader)
    {
        var lines = ReadLines(reader);
        var module = new MachineCodeModule();

        var functionChunks = SplitIntoFunctions(lines);

        foreach (var (funcName, funcLines) in functionChunks)
        {
            var function = module.CreateFunction(funcName);
            ParseFunction(function, funcLines);
        }

        return module;
    }

    private static void ParseFunction(MachineCodeFunction function, List<(int LineNumber, string Text)> lines)
    {
        // Pass 1: collect local label names for forward-reference resolution.
        var labelNames = new HashSet<string>();
        foreach (var (_, text) in lines)
        {
            if (TryParseLocalLabel(text, out var label))
                labelNames.Add(label!);
        }

        // Pass 2: emit entries.
        foreach (var (lineNumber, text) in lines)
        {
            if (TryParseLocalLabel(text, out var label))
            {
                function.EmitLabel(label!);
                continue;
            }

            function.Body.Add(ParseInstruction(text, lineNumber, labelNames));
        }
    }

    private static MachineCodeInstruction ParseInstruction(
        string text, int lineNumber, HashSet<string> localLabels)
    {
        var span = text.AsSpan().TrimStart();
        var mnemonicEnd = 0;
        while (mnemonicEnd < span.Length && !char.IsWhiteSpace(span[mnemonicEnd]))
            mnemonicEnd++;

        var mnemonic = span[..mnemonicEnd].ToString().ToUpperInvariant();
        var rest = span[mnemonicEnd..].TrimStart().ToString();

        var (mode, operand) = ParseOperand(rest, mnemonic, lineNumber, localLabels);

        if (!_reverseTable.TryGetValue((mnemonic, mode), out var opcode))
            throw new FormatException($"Line {lineNumber}: unknown instruction '{mnemonic}' with addressing mode {mode}");

        return operand == null
            ? new MachineCodeInstruction(opcode, [])
            : new MachineCodeInstruction(opcode, [operand]);
    }

    private static (AddressingMode Mode, MachineCodeOperand? Operand) ParseOperand(
        string text, string mnemonic, int lineNumber, HashSet<string> localLabels)
    {
        if (string.IsNullOrEmpty(text))
            return (AddressingMode.Implied, null);

        // Immediate: #$XX or #<sym (low half) or #>sym (high half)
        if (text.StartsWith('#'))
        {
            var body = text[1..];
            if (body.StartsWith('<'))
            {
                var (name, off) = SplitSymbolOffset(body[1..]);
                return (AddressingMode.Immediate, new MachineCodeOperand.ExternalRef(name, SymbolHalf.LowByte, off));
            }
            if (body.StartsWith('>'))
            {
                var (name, off) = SplitSymbolOffset(body[1..]);
                return (AddressingMode.Immediate, new MachineCodeOperand.ExternalRef(name, SymbolHalf.HighByte, off));
            }
            var value = ParseHex(body, lineNumber);
            return (AddressingMode.Immediate, new MachineCodeOperand.Immediate(value));
        }

        // Indirect variants: ($XX,X)  ($XX),Y  ($XXXX)
        if (text.StartsWith('('))
        {
            var inner = text[1..];
            if (inner.EndsWith(",X)", StringComparison.OrdinalIgnoreCase))
            {
                var value = ParseHex(inner[..^3], lineNumber);
                return (AddressingMode.IndirectX, new MachineCodeOperand.Immediate(value));
            }
            if (inner.EndsWith("),Y", StringComparison.OrdinalIgnoreCase))
            {
                var value = ParseHex(inner[1..^3], lineNumber);
                return (AddressingMode.IndirectY, new MachineCodeOperand.Immediate(value));
            }
            if (inner.EndsWith(')'))
            {
                var value = ParseHex(inner[..^1], lineNumber);
                return (AddressingMode.Indirect, new MachineCodeOperand.Immediate(value));
            }
            throw new FormatException($"Line {lineNumber}: malformed indirect operand '{text}'");
        }

        // Hex address: $XX or $XXXX with optional ,X / ,Y
        if (text.StartsWith('$'))
        {
            string addrPart;
            string? index = null;

            var commaIdx = text.IndexOf(',');
            if (commaIdx >= 0)
            {
                addrPart = text[..commaIdx];
                index = text[(commaIdx + 1)..].ToUpperInvariant();
            }
            else
            {
                addrPart = text;
            }

            var hexDigits = addrPart.Length - 1;
            var isZeroPage = hexDigits <= 2;
            var value = ParseHex(addrPart, lineNumber);

            var mode = (isZeroPage, index) switch
            {
                (true,  "X")  => AddressingMode.ZeroPageX,
                (true,  "Y")  => AddressingMode.ZeroPageY,
                (true,  null) => AddressingMode.ZeroPage,
                (false, "X")  => AddressingMode.AbsoluteX,
                (false, "Y")  => AddressingMode.AbsoluteY,
                (false, null) => AddressingMode.Absolute,
                _ => throw new FormatException($"Line {lineNumber}: unexpected index register '{index}'"),
            };

            return (mode, new MachineCodeOperand.Immediate(value));
        }

        // Local label: .name
        if (text.StartsWith('.'))
        {
            var labelName = text[1..];
            if (!localLabels.Contains(labelName))
                throw new FormatException($"Line {lineNumber}: undefined label '.{labelName}'");
            var isBranch = IsBranchMnemonic(mnemonic);
            return (isBranch ? AddressingMode.Relative : AddressingMode.Absolute,
                    new MachineCodeOperand.LabelRef(labelName));
        }

        // External symbol reference, optionally with a `+N` / `-N` byte offset.
        var addressingMode = IsBranchMnemonic(mnemonic) ? AddressingMode.Relative : AddressingMode.Absolute;
        var (symName, symOff) = SplitSymbolOffset(text);
        return (addressingMode, new MachineCodeOperand.ExternalRef(symName, SymbolHalf.Full, symOff));
    }

    // Splits a `name+N` / `name-N` symbol token into its name and signed byte
    // offset (offset 0 when no `+`/`-` suffix is present). The sign character
    // must follow at least one name character so a leading-`-` is never treated
    // as an offset separator.
    private static (string Name, int Offset) SplitSymbolOffset(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '+' || text[i] == '-')
                return (text[..i], int.Parse(text[i..]));
        }
        return (text, 0);
    }

    private static bool IsBranchMnemonic(string mnemonic) =>
        mnemonic is "BEQ" or "BNE" or "BCC" or "BCS" or "BMI" or "BPL" or "BVC" or "BVS";

    private static long ParseHex(string text, int lineNumber)
    {
        if (!text.StartsWith('$'))
            throw new FormatException($"Line {lineNumber}: expected hex value starting with '$', got '{text}'");
        if (!long.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out var value))
            throw new FormatException($"Line {lineNumber}: invalid hex value '{text}'");
        return value;
    }

    private static bool TryParseLocalLabel(string text, out string? label)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('.') && trimmed.EndsWith(':'))
        {
            label = trimmed[1..^1];
            return true;
        }
        label = null;
        return false;
    }

    private static List<(string FunctionName, List<(int LineNumber, string Text)> Lines)> SplitIntoFunctions(
        List<(int LineNumber, string Text)> lines)
    {
        var result = new List<(string, List<(int, string)>)>();
        List<(int, string)>? current = null;

        foreach (var (lineNumber, text) in lines)
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('.') && trimmed.EndsWith(':'))
            {
                var funcName = trimmed[..^1];
                current = [];
                result.Add((funcName, current));
                continue;
            }
            current?.Add((lineNumber, text));
        }

        return result;
    }

    private static List<(int LineNumber, string Text)> ReadLines(TextReader reader)
    {
        var result = new List<(int, string)>();
        var lineNumber = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            var commentIdx = line.IndexOf(';');
            if (commentIdx >= 0)
                line = line[..commentIdx];

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            result.Add((lineNumber, trimmed));
        }

        return result;
    }

    private static Dictionary<(string, AddressingMode), int> BuildReverseTable()
    {
        var table = new Dictionary<(string, AddressingMode), int>();
        for (var opcode = 0; opcode <= 0xFF; opcode++)
        {
            var info = MOS6502InstructionInfo.Instance.TryGet(opcode);
            if (info == null) continue;
            table.TryAdd((info.Mnemonic, info.Mode), opcode);
        }
        return table;
    }
}
